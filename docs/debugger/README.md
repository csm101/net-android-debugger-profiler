# net-android-debugger

This project now lives in the `net-android-debugger-profiler` monorepo
(`C:\Athens\GitHub\net-android-debugger-profiler`, GitHub `mca-software/net-android-debugger-profiler`,
private) as its debugger component: sources under `src/NetAndroidDebugger.*`, this document
under `docs/debugger/`. Repository-level state lives in the root `TASK_RESUME.md`; the shared
rules in the root `CLAUDE.md`, the component's own in `src/NetAndroidDebugger.Core/CLAUDE.md`.
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
[Delphi Win64 debugger](../../../delphi-visual-studio-code-debugger) — same
two-frontends-over-one-core layout, same development methodology.

## Quick start from sources

There is no package to install and no marketplace entry: this is proprietary
software in a private repository, so everything starts from a clone. Steps 1-3
are once per machine.

### 1. Clone and build

```powershell
git clone --recurse-submodules git@github.com:mca-software/net-android-debugger-profiler.git
cd net-android-debugger-profiler
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
register-mcp-debugger.cmd
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
vscode\install-vscode-extension.cmd
```

Only needed for VS Code, and only because VS Code refuses to run a debug adapter
named directly in `launch.json` — the debug *type* has to come from an extension.
The script junctions `vscode/net-android-debugger` into the user's
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

adb itself does not have to be on PATH. The debugger looks for it in this order:
an `adbPath` given in the call or in `launch.json`, then `NAD_ADB_PATH` (the
executable or its SDK / platform-tools folder), then the SDK named by
`ANDROID_HOME` or `ANDROID_SDK_ROOT`, then the SDK directory the .NET Android
workload and Android Studio record in the registry, then the SDK's default
folders, and PATH last. A machine set up by Visual Studio needs nothing: the
workload's registry entry is enough. A path that is given but wrong is an error,
never replaced by a guess; `list_devices` is where a fresh machine finds out,
and its message lists every place that was tried.

### 5. What to enable on the device

Debugging needs **USB debugging** in Developer options, and that is all it needs
on stock Android and on the emulator. Two more things are optional, and each one
is refused by some vendors until a further switch is on. The debugger keeps
working without them; only the feature that depends on them is lost, and the
error says which switch to flip.

| Feature | Needs | Stock Android | Xiaomi / MIUI |
|---|---|---|---|
| Debugging: attach, breakpoints, inspection | USB debugging | on by itself | on by itself |
| Deploying from the debugger (`deploy: true`) | installing over adb | on by itself | Developer options › **Install via USB** (MIUI asks for a Mi account, on some models for a SIM). Even with it on, **the phone shows a confirmation dialog for every install**, for a few seconds; unanswered, the install fails as `INSTALL_FAILED_USER_RESTRICTED` ("Install canceled by user"). Keep an eye on the screen the first time. Turning off **Turn on MIUI optimization** removes the dialog. |
| Screen tools: `tap_screen`, `swipe_screen`, `press_key`, `type_text` | adb input injection | on by itself | Developer options › **USB debugging (Security settings)** — "allow granting permissions and simulating input via USB debugging". Off, every input tool fails with `INJECT_EVENTS permission`, and the message names this switch. Same Mi-account requirement. |
| Screen tools: `capture_screenshot`, `get_ui_hierarchy` | nothing beyond USB debugging | works | works |

`check_device_control` probes all of this on a device and reports it in one
call, without changing anything on the device.

Other vendors gate the same two things behind switches of their own in Developer
options; the MIUI column is the one verified here (Redmi Note 8 Pro, MIUI 12.5).
A screen that is off or locked has no hierarchy to read: `wake_screen` turns it
on and dismisses a lock screen that has no PIN.

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

`vscode/DAP_CLIENTS.md` documents every launch argument, and configures
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

## Why a debugger written from scratch

For one of the two frontends, the prior art is real and should be acknowledged.
Microsoft's [C# Dev Kit](https://code.visualstudio.com/docs/csharp/cs-dev-kit-faq)
with the .NET MAUI extension already debugs .NET Android apps in VS Code, and
Visual Studio and Rider have done so for years. If the goal were pressing F5 in
an editor, this project would not exist, and it does not try to match those on
editor integration: the DAP frontend is a by-product of a frontend-neutral core,
not a competitor.

What does not exist is a debugger an autonomous agent can drive. That is the
project: semantic operations exposed as MCP tools, answers shaped for a token
budget rather than for a debug pane, and stops that report what happened instead
of handing back a tree to be walked one request at a time.

Wrapping an existing debugger instead was considered and is closed on both
counts.

**Licensing.** C# Dev Kit is closed source and not redistributable. It is free
for personal, academic and open-source use and for commercial teams of up to
five developers; beyond that it requires a Visual Studio Professional
subscription, and an Enterprise — more than 250 users or a million dollars in
revenue — may not use it outside open source and education at all. Microsoft's
debugger components carry a further restriction: they are licensed to run only
with Microsoft's own IDEs and, for VS Code, only with the build Microsoft
distributes — a constraint `vsdbg` also enforces with a handshake. An adapter
that may only be driven inside someone else's IDE cannot sit under a server of
our own. See
[the .NET Core Debugger licensing note](https://github.com/OmniSharp/omnisharp-vscode/wiki/Microsoft-.NET-Core-Debugger-licensing-and-Microsoft-Visual-Studio-Code).

**Architecture.** Visual Studio and Rider are not a licensing problem — driving
either locally is ordinary use — but neither exposes its debugger as a service.
Visual Studio's `EnvDTE`/`Debugger2` COM automation exists and could in principle
be scripted, at the cost of keeping a GUI IDE alive for a headless server to talk
to; Rider's debug backend is internal and undocumented for third parties.

That leaves the MIT sources — [mono/debugger-libs](https://github.com/mono/debugger-libs),
which is what the reference implementations use as well — as the only reusable
foundation, and an engine of our own above them.

Building it that way also bought things a general-purpose debugger does not
offer, because the consumer is an agent working through a noisy real application:
an ordered per-exception rule engine whose actions include logging and continuing
rather than only breaking, automatic attach to every process of the app
recognised by uid rather than by name, compact snapshots that fold a stop's stack
and locals into one answer, and a launch that deduces the application project,
its `ApplicationId` and the device from the solution.

The standing cost is maintenance: the Mono soft debugger protocol, .NET releases
and Android API levels all move, and nobody else is keeping this current.

## Using the MCP server with Claude Code

`register-mcp-debugger.cmd` registers the server once at user scope, as
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

The agent can also drive the screen, so it reaches the point worth debugging by
itself instead of asking someone to tap through the app: `capture_screenshot`
returns the screen as an image (it works while the app is stopped at a
breakpoint), `get_ui_hierarchy` lists the views with their ids, texts and centre
coordinates, and `tap_screen(resourceId: "login_button")` or
`tap_screen(text: "Login")` taps the one view a selector identifies. There are
also `swipe_screen`, `press_key`, `type_text` (ASCII, into the focused field) and
`wake_screen`. Everything goes through adb: no agent app on the device, nothing
inside the debuggee. All coordinates are physical pixels, the same in screenshots,
the hierarchy and taps. The one thing to know is that a suspended app has no
live main thread: `get_ui_hierarchy` fails, in words, and a tap or key would
block until the app consumes it, so the input tools refuse while the session is
stopped — resume first, or take a screenshot, which works at any time. Section 5
above says what a vendor may require before input injection works;
`check_device_control` reports it for a device.

## Using the DAP adapter from an editor

The same engine is also exposed as a Debug Adapter Protocol adapter, published
alongside the MCP server. A DAP client runs it directly, and nothing registers it
— the client owns that command line:

```
dotnet "%LOCALAPPDATA%\net-android-debugger\NetAndroidDebugger.Dap.dll"
```

`vscode/DAP_CLIENTS.md` has the launch and attach argument reference, a
ready-made nvim-dap configuration, and the behaviours worth knowing before wiring
a client up. VS Code needs the extension from step 3 of the quick start, because
a debug *type* can only come from an extension;
`vscode/net-android-debugger/` is a small one that contributes
`net-android` and holds no logic.

`THIRD-PARTY-NOTICES.txt` lists every third-party component shipped with the
binaries, with its licence in full; `register-mcp-debugger.cmd` copies it next to them.

## Layout

```
src/NetAndroidDebugger.Core    engine library (frontend-neutral, JSON-free)
src/NetAndroidDebugger.Mcp     MCP stdio server frontend
src/NetAndroidDebugger.Dap     Debug Adapter Protocol frontend
tests/NetAndroidDebugger.Tests integration test suite (xUnit)
ThirdParty/                    vendored upstream (mono/debugger-libs)
DevTools/                      argv-driven diagnostic probes
TestTarget/Debugger/           minimal Android app used by the test suite
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
