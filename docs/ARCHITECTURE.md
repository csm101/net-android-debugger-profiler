# Architecture of the monorepo

How the two products relate, what they duplicate today, and the two decisions this
repository exists to make possible. Each product's own architecture (modules, session
model, frontend contracts, schema) stays where it was: `docs/debugger/ARCHITECTURE.md`
and `docs/profiler/ARCHITECTURE.md`.

## Component map

```
 agents (Claude Code)      editors (VS Code, nvim-dap)        agents          scripts, CI        people
        |                          |                             |                |                |
 NetAndroidDebugger.Mcp   NetAndroidDebugger.Dap        NetAndroidProfiler.Mcp   NetAndroidProfiler.Cli   gui/NapGui.exe (Delphi)
        \                          /                             \             (nap, nap serve)  /  reads session.db
         \                        /                               \                |            /   drives nap serve
          NetAndroidDebugger.Core (SDB engine,                     NetAndroidProfiler.Core (collection, analysis,
          launch, exception rules, screen)                         SQLite results, weaving, control contract)
                   |                                                  |                       \
      ThirdParty/debugger-libs (submodule:              NetAndroidProfiler.Collector          NetAndroidProfiler.Weave (nap-weave)
      Mono.Debugger.Soft, Mono.Debugging,               (runtime library the woven            + build/NetAndroidProfiler.Weaving.targets
      Mono.Debugging.Soft)                              code calls)
                   \                                                  /
                    ----------- adb, device state, app environment -----------
                                 today: one copy in each Core
                                 planned: src/NetAndroid.Device (decision 1)
```

| Piece | Path | Notes |
|---|---|---|
| Debugger engine | `src/NetAndroidDebugger.Core` | Frontend-neutral, JSON-free. `Adb/`, `Device/`, `Launch/`, `Engine/`, `Model/` |
| Debugger frontends | `src/NetAndroidDebugger.Mcp`, `src/NetAndroidDebugger.Dap`, `src/NetAndroidDebugger.Shared` (linked sources) | Registered as `net-android-debugger`; published to `%LOCALAPPDATA%\net-android-debugger` |
| Vendored debugger libraries | `ThirdParty/debugger-libs` (git submodule, fork `csm101/debugger-libs`), `ThirdParty/Mono.Debugging.overrides` | Read-only upstream; the one local patch is documented in the debugger's ARCHITECTURE |
| Profiler engine | `src/NetAndroidProfiler.Core` | Frontend-neutral. `Devices/`, `Apps/`, `Collection/`, `Analysis/`, `Store/`, `Weaving/`, `Sessions/`, `Projects/` |
| Profiler frontends | `src/NetAndroidProfiler.Mcp`, `src/NetAndroidProfiler.Cli` (`nap`), `gui/` | Registered as `net-android-profiler`; published to `%LOCALAPPDATA%\net-android-profiler` |
| Profiler weaving | `src/NetAndroidProfiler.Weave`, `src/NetAndroidProfiler.Collector`, `build/` | Build-time and on-device IL weaving |
| Test apps | `TestTarget/Debugger` (`net.androiddebugger.testtarget`), `TestTarget/Profiler` (`com.mcasoftware.testtarget`) + `TestTarget/TestTarget.Support` | Two apps on purpose for now; unifying them is a later task |
| Suites | `tests/NetAndroidDebugger.Tests`, `tests/NetAndroidProfiler.Tests`, `tests/WeaveSample` | Catalogued in each product's `TEST_CATALOG.md` |
| Example | `examples/ProfileMeExample.sln` | The profiler GUI's tutorial app, shipped as part of the repository |
| Probes and scripts | `DevTools/` | Both products' probes; `DevTools/scripts` shared (`ensure-emulator.sh`) |
| Editor assets | `vscode/` | The debugger's VS Code extension and DAP client notes |
| Solutions | `NetAndroidDebuggerProfiler.slnx`, `NetAndroidDebugger.slnx`, `NetAndroidProfiler.slnx` | Everything; the debugger alone; the profiler alone (example included) |

## What the two Cores duplicated (until 2026-09-06)

The device layer was written twice, once per product, and the two copies overlapped on the
same adb mechanics while each had pieces the other lacked. This is what decision 1 unified:

- **adb client.** `src/NetAndroidDebugger.Core/Adb/AdbClient.cs` and
  `src/NetAndroidProfiler.Core/Devices/AdbClient.cs` both run adb, shell commands, list
  devices, get/set properties, force-stop and read logcat. The debugger's adds port
  forwarding, launcher-activity resolution, package uid and process listing and a logcat
  boundary; the profiler's adds push/pull, `run-as`, reverse ports, `pidof`, `exec-out`
  and a richer `DeviceInfo` (API level, ABI, AVD name).
- **finding adb.** The debugger's `Adb/AdbLocator.cs` is the richer one (`NAD_ADB_PATH`,
  `ANDROID_HOME`/`ANDROID_SDK_ROOT`, the registry entries the Android workload and Android
  Studio write, the SDK's default folders, PATH last; a named-but-wrong path is an error,
  never a fallback). The profiler's `Devices/ToolLocator.FindAdb` is a shorter list.
- **running processes.** The profiler's `Devices/ProcessRunner.cs` (stdin always redirected,
  because a child inheriting stdin eats an stdio frontend's request stream) versus the
  debugger's private helpers inside `AdbClient`.
- **device-global Mono properties with backup and restore.** The debugger writes
  `debug.mono.extra` (the SDB agent's address; `Launch/AndroidLauncher.cs`, with
  `ForeignDebugPropertyWarning` to notice a value left by another tool); the profiler writes
  `debug.mono.profile` and the app's environment override file
  (`files/.__override__/<abi>/environment`) with backup and restore
  (`Collection/AppEnvironment.cs`, `Collection/EnvironmentOverrideFile.cs`).
- **the screen layer** exists only in the debugger (`Device/DeviceControl.cs`,
  `Device/UiHierarchy.cs`: screenshots, UI hierarchy, taps, keys, text, capabilities).
- **infrastructure:** two TestTarget apps, two sets of probes, one emulator script
  (the debugger's), two sets of empirical notes.

## Decision 1 (done 2026-09-06): `src/NetAndroid.Device`

One `net10.0` class library, **no package references** (BCL only, so it can never conflict
with the two Cores' packages: they pin different `Mono.Cecil` versions, 0.10.1 and 0.11.6),
namespace `NetAndroid.Device`, referenced by both Cores. It holds:

- `ProcessRunner` (from the profiler) as the single way both products run external processes;
- `AdbLocator` (from the debugger) as the single way adb is found; the profiler's
  `ToolLocator.FindAdb` and its prerequisite message delegate to it, the rest of
  `ToolLocator` (dsrouter, dotnet, weaving targets, tool install) stays in the profiler;
- one `AdbClient`: the union of the two public APIs, one `DeviceInfo` record with every field
  either side had, one `AdbResult`/`AdbException`; existing method names are kept and
  overloads added rather than renamed, so the Cores change only usings and constructor calls;
- `DeviceControl`, `UiTree` and the screenshot/UI/input types, unchanged;
- `AppEnvironment` + `EnvironmentOverrideFile`, generalized into a small "device-side state
  with backup and restore" abstraction that also covers the two `debug.mono.*` properties:
  one `DevicePropertyOverride` (set, remember the previous value, restore, detect a foreign
  value) used by the debugger's launcher for `debug.mono.extra` and by the profiler for
  `debug.mono.profile`; the debugger's `ForeignDebugPropertyWarning` becomes the shared way
  each product notices a mark left by the other or by a stale run;
- one constants file for the device-global names both products touch.

What stays where it is: `AndroidLauncher` (SDB-specific) in the debugger Core; dsrouter,
EventPipe collection and `AppInspector`'s profiling prerequisites in the profiler Core, with
`AppInspector` calling the shared client for the adb work. A piece moves only if both Cores
would call it or if it is pure adb/device mechanics with no debugger or profiler knowledge.

Rule: **device access only through `NetAndroid.Device`** for both Cores. No
`Process.Start("adb")`, no second adb client, no direct `setprop` outside it. The two
products' `CLAUDE.md` files carry the same rule.

Landed on 2026-09-06 in five strangler steps, one commit each, both products' suites run
after each: the process runner and the locator; the unified client (`AdbResult` keeps
`Succeeded` and a `Success` alias, `AdbException` derives from `ToolException`, the locator
throws `AdbNotFoundException`, which the debugger's session turns back into its
`LaunchException`); the screen layer; `DevicePropertyOverride` with the app environment
(the debugger's launcher clears `debug.mono.extra` at shutdown as before, `ClearAsync`, which its
tests specify, while the profiler restores `debug.mono.profile`, `RestoreAsync`; the profiler logs a mark left on
`debug.mono.profile` before taking it over); `DeviceGlobals` and these documents. Its tests:
`tests/NetAndroid.Device.Tests`, catalogued in `docs/TEST_CATALOG.md`.

## Decision 2 (done 2026-09-06): one unified MCP server, `src/NetAndroid.Mcp`

Each product still registers its own MCP server (`net-android-debugger`, `net-android-profiler`)
and both keep working unchanged. Next to them, `src/NetAndroid.Mcp` is one server over both
Cores, registered as `net-android` by `register-mcp.cmd` (publish folder
`%LOCALAPPDATA%\net-android`). It is a third thin frontend, not a third engine:

- **The tools are the products' tools.** The project references the two product MCP projects
  as libraries and registers their tool classes as they are (`ToolCatalog`, by reflection over
  the `[McpServerTool]` methods, one instance of each class for the process). A tool added to a
  product appears in the unified server without any change. A tool added to *both* products
  has to be added to `SharedTools` instead, or the server refuses to start on the duplicate name;
  today those are `list_devices`, `list_app_projects` and `get_app_output`. `SharedTools` answers
  them once: the two listings through the profiler's tools (the debugger's output is a subset of
  theirs), the app output from the debug session while one is active and from the device's
  logcat (`deviceSerial`, `packageName`) otherwise.
- **The device-global state has one owner.** `DeviceArbiter` is a call-tool filter in front of
  the tools that start an engine on a device: `profile_run` and `profile_start` are refused while
  a debug session holds the device (it holds `debug.mono.extra` there, so an app started for
  profiling would wait for a debugger), `launch_app`, `launch_from_config` and `attach_to_app`
  while a profiling session holds it (`debug.mono.profile` and the app's override environment: an
  app launched for debugging would connect to the profiler as well). The refusal names the
  session to stop. The one start let through is the profiler attaching to the very app the debugger
  runs (`launch: attach`, the same package, an explicit device): it takes nothing device-global and
  is how a debugged app gets profiled from a breakpoint on (verified 2026-09-08,
  `DebugAndProfileTogetherTests`; the profiler's ANDROID_PROFILING_NOTES has the flow). A call that
  names no device is refused whenever the other engine holds any, since the tool would then pick
  one on its own. The decision is a pure function
  (`DeviceArbiter.Refusal`) with its own tests; the live state comes from the two session hosts.
  Decision 1 made the *noticing* of a foreign mark shared (`DevicePropertyOverride.ForeignValueWarning`,
  still in force for sessions started outside this process); this is the arbitration.
- **Both session hosts live in one process**, each product's own, as singletons; the engines do
  not know about each other. `Mono.Cecil` resolves to 0.11.6 in this process (the profiler's
  weaver) while `Mono.Debugger.Soft` was compiled against 0.10.1; the debugger's MCP end-to-end
  suite run through the unified server (`NAD_MCP_SERVER_DLL`) is the check that this holds.

Tests: `tests/NetAndroid.Mcp.Tests` (root `docs/TEST_CATALOG.md`, section G). Both products'
MCP suites can be pointed at the unified server (`NAD_MCP_SERVER_DLL`, `NAP_MCP_SERVER_DLL`)
for the device-level checks.

Not done, on purpose: one device selection and one app discovery for both engines (each Core
keeps its `AppProjectFinder` and its device choice; unifying them is a Core refactoring, not a
frontend one), and retiring the two product registrations (the user's choice, when the unified
server has proved itself in daily use; `register-mcp.cmd` says how).
## Decision 3 (done 2026-09-08): one package, one plugin, one skill

What ships is one folder, built by `build/package.ps1` (the profiler's packaging script, extended):
the unified server and its dependencies in `bin/` (next to the profiler's own server, `nap` and
`nap-weave`), dotnet-dsrouter in `tools/`, the weaving targets in `build/`, the Delphi GUI in `gui/`,
and at the root the files that make the folder a Claude Code plugin: `.claude-plugin/plugin.json`
(the `net-android` server registration, `dotnet ${CLAUDE_PLUGIN_ROOT}/bin/NetAndroid.Mcp.dll`),
`.claude-plugin/marketplace.json` (the folder as a one-plugin marketplace, so a local install is two
`claude plugin` commands) and `skills/net-android/`. Sources of the plugin files: `plugin/` in the
repository, copied as they are; the version is stamped at packaging time from `NapVersion`, which the
unified server now carries too. `install.cmd` (Windows) registers the plugin, falling back to a plain
`claude mcp add` plus a copy of the skill under the user's skills, and creates the desktop shortcut to
the GUI; `/remove` undoes it. The GUI is distributed only: driving it from the server as a results
viewer is a later phase, and this layout (`gui/` next to `bin/`) is what that will rely on.

The skill is written for anyone using the server with a .NET for Android or MAUI app: it assumes
the server, `dotnet` with the android workload and adb, nothing of this repository or of any
particular app. It is bound to the server by `tests/NetAndroid.Mcp.Tests/SkillTests`: every tool
it names exists in the server's tool list, the tools that start and end an engine are all covered,
the plugin files parse and point at the shipped server, and a list of forbidden words keeps product,
machine and repository names out. `build_app` (profiler frontend, over Core's `AppBuilder` with a
one-word `BuildPurpose`) exists so that the skill never carries an msbuild command line.

Windows first: the server and the plugin registration are portable .NET, the GUI and the installer
are Windows-only, other systems are untested.

## Decision 4 (done 2026-09-08): the GUI as a renderer the server drives

An agent that has found something often needs to show it: a call graph, the growth between
two heap snapshots, a hot method beside its source. Capturing the screen is the wrong way -
it takes whatever is on the user's desktop, needs the window in front, and `PrintWindow`
returns black on this skinned VCL window. So the GUI draws its own panels instead.

`NapGui.exe --control` speaks line-delimited JSON over its standard input and output
(`gui/src/uGuiControl.pas`, the commands; `gui/src/uGuiRender.pas`, the drawing).
`src/NetAndroidProfiler.Core/Gui/GuiChannel.cs` owns that process the way `DsRouterProcess`
owns dsrouter, and the `gui_open` / `gui_view` / `gui_capture` / `gui_show` / `gui_close`
tools (`src/NetAndroidProfiler.Mcp/GuiTools.cs`, over a `GuiHost` singleton because a tool
class is disposed after its call) are the thin frontend. The unified server picks them up
with no change, as it does every profiler tool.

What this buys, and its rules:

- **The window is not shown.** The picture comes back in the answer, which works when the
  person asking is not at that machine, and nothing of their desktop can be in it. The
  window goes on screen only through `gui_show`, when they ask.
- **A driven window is not somebody's window**: it ignores the saved layout and never
  writes one, so pictures are reproducible and nobody's arrangement is disturbed.
- **The GUI is optional**: `ToolLocator.FindGui` reports it as a prerequisite that may be
  missing, and every finding is still an answer in words without it.
- Windows only, like the GUI itself.

Not in this phase: driving a GUI the user opened by hand (the channel is per server-owned
process; another transport can be put in front of the same commands), and starting
profiling runs from the GUI, which the profiling tools already do.
## Conventions the products share

- `NetAndroidDebuggerProfiler.slnx` builds everything; the per-product solutions are for
  daily work and fast test runs. Tests: `dotnet test NetAndroidDebugger.slnx`,
  `dotnet test NetAndroidProfiler.slnx` (the profiler's device classes carry
  `Category=Device`; the debugger's device classes carry no trait and fail without a device).
- Living documents per product under `docs/<component>/`; root documents here.
- `Directory.Build.props` at the root: shared company and copyright; the profiler's
  `NapVersion` block scoped to the `NetAndroidProfiler.*` projects.
- `THIRD-PARTY-NOTICES.txt` at the root has one part per product; both products' register
  scripts copy the whole file beside their binaries.
