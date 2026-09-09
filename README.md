# net-android-debugger-profiler

A **debugger** and a **profiler** for .NET for Android applications — C# on MonoVM, .NET MAUI
apps included — driven by AI agents through MCP, by editors through the Debug Adapter Protocol,
and by people through a desktop GUI and a command line.

> **First release, September 2026.** The MCP server is the finished part: its whole tool surface
> has been run end to end against a real application on a physical device. The GUI works and is
> in daily use, but it is still work in progress, and there is no proper manual yet - this README,
> the documents under `docs/` and the skill the server ships with are what exists. Bug reports are
> very welcome: the author is away until 24 September 2026 and will answer them on his return.

## What is in the package

Three separate things over one engine.

### 1. An MCP server, with the skill that drives it

For Claude Code and any other agent that speaks MCP. Through it an agent can:

- **take the device**: launch and stop the app, tap, swipe, type, press keys, and read what is
  on screen - a screenshot and the view hierarchy with the ids, texts and bounds of what is
  there, so it acts on what the app actually shows rather than on coordinates it guessed;
- **debug**: run the app under the Mono soft debugger, set breakpoints (conditional, by hit
  count, or logpoints that never stop the app), step, read locals, evaluate expressions, walk
  call stacks and threads, and catch exceptions **first-chance** - by type or by ordered rules
  that ignore the noisy ones and break on the rest - not only when they go unhandled;
- **read what the app says**: logcat and the app's own debug stream in one place -
  `Debug.WriteLine`, `Console`, stdout and stderr all arrive as app output;
- **find memory leaks and GC pressure**: live heap by type, the growth between two snapshots,
  and allocations by type and by allocating method;
- **find bottlenecks**: CPU sampling for where the time goes, deterministic instrumenting for
  how often and how long, on the same installed app.

The skill ships with the server: it is the operating guide that tells the agent which mode
answers which question, how to read the numbers, and the traps to avoid
([plugin/skills/net-android/SKILL.md](plugin/skills/net-android/SKILL.md)). With it, Claude Code
debugs and profiles a .NET Android application on its own - on a headless emulator, without a
window on anybody's screen and without a human driving the app. When a finding is easier seen
than told, it has the GUI draw the panel and shows the picture.

### 2. A DAP debugger for C#, written from scratch

Installable in VS Code beside, or instead of, the official extension. It is an offshoot of what
the MCP server needed rather than a rival: it does not aspire to replace the official C# Dev Kit
debugger. Two things make it worth having anyway:

- it is MIT, so it carries none of the licence restrictions the official one comes with;
- it debugs **every process the app starts**, not only the one that owns the main window -
  services, crash reporters and anything with its own `android:process` are attached as they
  appear, each with its own breakpoints and stacks.

### 3. A standalone GUI for the profiler (Windows)

For running sessions by hand, with no agent and no editor. It builds and instruments the
application itself - point it at a solution, a project or a folder of sources and it fills in
the package, the build output and the callspec, installs the app and runs the session - so it
needs no development environment beside it. It opens any session any frontend recorded.

### The profiling modes, and what each costs

| Mode | What it gives | What it costs |
|---|---|---|
| **CPU sampling** | Where the time goes: hottest methods, call tree, callers and callees, per thread, CPU time apart from blocked time | Nothing to the app: no rewriting, and it can attach to an app that is already running. ~1 ms samples, so short calls are statistical; no per-line figures on MonoVM, and very short callees are folded into their caller |
| **Instrumenting, call tree** (default) | Exact call counts and times per method, with the call tree kept in the app: callers, callees, critical path, min and max | The app keeps a tree instead of a log, so it is the cheapest instrumenting; the order of calls and each single duration are not kept. Needs a narrow callspec and a restart |
| **Instrumenting, every call** | Every call recorded: their order and each duration, plus allocations attributed to the allocating method | Larger traces and more overhead than the tree; same callspec and restart |
| **Instrumenting, runtime provider** | The runtime's own enter/leave, with no assembly rewritten | Crashes .NET 9 runtimes and degrades on a device that has been profiled for a long time: the rewriting engines are the default for good reason |
| **Heap snapshots** | Live objects by type, and the growth between two snapshots - the leak candidates at the top | Seconds of pause per snapshot on a large heap; no rewriting and no restart |
| **Allocations** | What is allocated, by type, and by the method that allocated it | Recorded during an instrumenting session, so it carries that session's cost |

Figures are per method. **Per-line instrumenting is planned, not yet supported**: sampling
cannot give it at all on MonoVM (the runtime reports no IL offset), while the weaver can and
will - `profile_annotate_source` shows a method's figures on its first line with its range
marked, and the GUI's callspec picker already lets a per-line choice be recorded, marked
"(planned)" until the collection catches up.

Everything runs against an app installed on an emulator or an attached device; nothing is
simulated, and no source is required beyond what the app was built with.
## Install

A release is one folder. Unpack it and run, on Windows:

```
install.cmd
```

That registers **one Claude Code plugin**, which brings the MCP server and the operating skill
together, and puts a shortcut to the profiler GUI on the desktop. `install.cmd /remove` undoes
both; `/no-shortcut` skips the shortcut; `/mcp-only` registers the server alone (plus a copy of
the skill under `%USERPROFILE%\.claude\skills`) for a Claude Code without plugin support.

By hand, on any system with Claude Code:

```
claude plugin marketplace add <the unpacked folder>
claude plugin install net-android@net-android
```

**What you need**: the .NET 10 runtime for the server and the tools; the .NET SDK with the
`android` workload to build an app for profiling or debugging; the Android platform-tools
(`adb` on PATH, or `ANDROID_HOME` / `ANDROID_SDK_ROOT`); a device or emulator with USB
debugging on. `dotnet-dsrouter` travels inside the package.

**What is in the package**: the unified MCP server and its dependencies (`bin/`), the profiler's
command line `nap` and its weaver, `dotnet-dsrouter` (`tools/`), the MSBuild targets for
build-time weaving (`build/`), the GUI (`gui/NapGui.exe`), and the plugin files that make the
folder installable — including the skill in `skills/net-android/`. Building a release:
`gui\build-gui.cmd` (RAD Studio) then `powershell -File build\package.ps1`; the details are in
[docs/profiler/PACKAGING.md](docs/profiler/PACKAGING.md).

## What it looks like

The report names the hot methods and shows their share; the call graph puts one of them between
its callers and its callees. Both pictures are of the example app's "slow search" screen, and
both were drawn by the GUI itself on request - the agent asks for a panel and gets the image
back, with no window on anyone's screen:

![The report panel: hottest methods of a sampling session, with their share of the samples](docs/images/gui-report.png)

![The call graph around the hot method, with its callers above and its sample counts](docs/images/gui-call-graph.png)

## The other two ways in

**The command line.** `nap doctor` says what the machine offers, `nap devices` lists them,
`nap run --package <id> --mode sampling --duration 20` profiles and prints where the result
database is. Every session is a SQLite database that any frontend - or any SQLite client - reads.

**VS Code.** The DAP extension lives in `vscode/` and is installed from this repository with
`vscode\install-vscode-extension.cmd`; `.vscode/launch.json` holds the launch configuration, and
the same file is what `launch_from_config` reads, so how an app is launched lives with its
sources rather than being restated per tool.
## The two products

| Product | What it is | Sources | Read first |
|---|---|---|---|
| **net-android-debugger** | Breakpoints, stepping, stack, locals, evaluation and an ordered exception rule engine over the Mono Soft Debugger protocol, plus screen tools (screenshot, UI hierarchy, tap, swipe, keys) so an agent can bring the app to the point worth debugging. Frontends: MCP server (`net-android-debugger`), DAP adapter, VS Code extension. | `src/NetAndroidDebugger.Core`, `.Mcp`, `.Dap`, `.Shared`; `ThirdParty/debugger-libs` (submodule); `vscode/` | [docs/debugger/README.md](docs/debugger/README.md) |
| **net-android-profiler** | CPU sampling, allocation and heap analysis, instrumenting through the runtime's Mono profiler provider or a runtime-independent IL weaver; results in a versioned SQLite database. Frontends: MCP server (`net-android-profiler`), `nap` (one-shot commands and the local control service), the DevExpress GUI `NapGui.exe`. | `src/NetAndroidProfiler.Core`, `.Mcp`, `.Cli`, `.Weave`, `.Collector`; `gui/`; `build/` | [docs/profiler/README.md](docs/profiler/README.md) |

Both are driven through **one MCP server**, `net-android` (`src/NetAndroid.Mcp`): every debugger
and profiler tool in one process, the three tools both products define answered once, and the
device-global Mono state arbitrated so that debugging and profiling never start on the same
device at the same time — with one exception, profiling the very app the debugger is running.
Why the two products share a repository, the shared device library `src/NetAndroid.Device` and
the unified server are in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Build from source

```powershell
git clone --recurse-submodules git@github.com:csm101/net-android-debugger-profiler.git
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

Working from the sources, each server is published and registered with Claude Code by its own
script (a Claude Code with all three registered sees every tool twice; `register-mcp.cmd` says
which two registrations to remove once the unified server is in use):

```
register-mcp.cmd               publishes to %LOCALAPPDATA%\net-android and registers net-android (both products, one server)
register-mcp-debugger.cmd      publishes to %LOCALAPPDATA%\net-android-debugger and registers net-android-debugger
register-mcp-profiler.cmd      publishes to %LOCALAPPDATA%\net-android-profiler and registers net-android-profiler
vscode\install-vscode-extension.cmd   installs the debugger's VS Code extension (junction into the extensions folder)
build-gui.cmd                  builds the profiler GUI, gui\NapGui.exe (RAD Studio with DevExpress and SynEdit)
build\package.ps1              builds the release package described under Install
```

## Test

```powershell
dotnet test NetAndroidDebugger.slnx                                  # the whole debugger suite (needs a device)
dotnet test NetAndroidProfiler.slnx --filter "Category!=Device"     # the profiler's recorded-trace tests
dotnet test NetAndroidProfiler.slnx                                  # plus the profiler's device tests
dotnet test tests/NetAndroid.Device.Tests/NetAndroid.Device.Tests.csproj   # the shared device layer (docs/TEST_CATALOG.md)
dotnet test tests/NetAndroid.Mcp.Tests/NetAndroid.Mcp.Tests.csproj         # the unified MCP server and the skill (docs/TEST_CATALOG.md)
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
src/NetAndroid.Mcp                                    the unified MCP server over both engines
plugin/                                               the Claude Code plugin: the skill and the registration files the package ships
ThirdParty/debugger-libs                              mono/debugger-libs, git submodule (MIT)
tests/NetAndroidDebugger.Tests, tests/NetAndroidProfiler.Tests, tests/WeaveSample
TestTarget/Debugger, TestTarget/Profiler (+ TestTarget.Support)   the two test apps
DevTools/                                             probes of both products; scripts/ shared
gui/                                                  the profiler's Delphi + DevExpress GUI
build/                                                packaging and build-time weaving targets
examples/                                             ProfileMeExample, the profiler GUI tutorial
vscode/                                               the debugger's VS Code extension and DAP client notes
docs/                                                 ARCHITECTURE.md, KNOWN_UNKNOWNS.md; docs/debugger/, docs/profiler/, images/
```

Each product keeps its living documents under `docs/<component>/`: `ARCHITECTURE.md`,
`PROJECT_STATE.md`, `TASK_RESUME.md`, `KNOWN_UNKNOWNS.md`, `TEST_CATALOG.md` and the
empirical notes (`ANDROID_ATTACH_NOTES.md`, `ANDROID_PROFILING_NOTES.md`). The root
`TASK_RESUME.md` tracks repository-level work.

## What you can expect

This is a tool built to do a job, published because it may do yours too. It is offered as
it is, with no promise of support, of answers, or of a release on any schedule. Issues and
pull requests are welcome and read; neither is a ticket.

One part cannot be built by everyone: the profiler's GUI is Delphi and needs RAD Studio
with DevExpress VCL and SynEdit. Its sources are here under the same license, but the
built `NapGui.exe` in a release is what to use without those. Everything else - both MCP
servers, the DAP adapter, `nap`, the weaver, the tests - builds with the .NET SDK alone.

## Licensing

MIT: see [LICENSE](LICENSE). Copyright MCA Software s.a.s. di Sirna Carlo & C.

Third-party dependencies are restricted to MIT, BSD and Apache-2.0 (plus MPL components
used unmodified in the GUI); GPL is excluded, so that nothing here raises a license
question for whoever uses it. `THIRD-PARTY-NOTICES.txt` lists, in one part per product,
every component the shipped builds carry, with the full license texts; both test suites
fail when a distributed package is missing from it. `ThirdParty/debugger-libs` is a
submodule of a fork of `mono/debugger-libs`, under its own MIT license.
