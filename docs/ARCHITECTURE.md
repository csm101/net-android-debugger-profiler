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

## What the two Cores duplicate today

The device layer was written twice, once per product, and the two copies overlap on the
same adb mechanics while each has pieces the other lacks:

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

## Decision 1 (in progress): `src/NetAndroid.Device`

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

Rule once the library exists: **device access only through `NetAndroid.Device`** for both
Cores. No `Process.Start("adb")`, no second adb client, no direct `setprop` outside it.

Method: strangler style, the smallest green step first, one commit per step, both suites
green after each step. The steps and their state are tracked in the root `TASK_RESUME.md`;
the profiler's `KNOWN_UNKNOWNS.md` U11 closes with a pointer here when the library lands.

## Decision 2 (later): one unified MCP server

Today each product registers its own MCP server (`net-android-debugger`,
`net-android-profiler`). Both touch the same device-global state (`debug.mono.extra`,
`debug.mono.profile`, the app's override environment file) and nothing coordinates them:
running both on the same app at once is not supported, and each only *notices* the other's
mark (decision 1 makes the noticing shared, not the arbitration). A single server over both
Cores would own the device state, expose both tool sets to the agent, share device
selection and app discovery, and register once. It is not started: it needs decision 1
first, and until it ships the two registration names, publish folders and scripts stay as
they are.

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
