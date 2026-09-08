# net-android as a Claude Code plugin

The folder this file sits in is a Claude Code plugin: it registers the `net-android` MCP
server (debugger and profiler for .NET for Android and .NET MAUI Android apps) and carries the
skill that tells an agent how to use it well. In the installation package the same folder also
holds the server (`bin/`), the profiler's helpers (`tools/`, `build/`) and the GUI (`gui/`).

## Install

From the unpacked package folder, on Windows:

```
install.cmd
```

It copies the package to `%LOCALAPPDATA%\Programs\net-android-<version>`, registers the plugin
with Claude Code from there (a local marketplace pointing at that folder, then the plugin from
it) and puts a shortcut to the GUI on the desktop. What you unpacked can then be deleted:
a registration points at an absolute path, and a package registered where it was unpacked stops
working the day that folder moves. `install.cmd /here` registers in place instead;
`install.cmd /remove` undoes everything, the installed copy included. Without the `claude`
command line the script says what to do by hand.

The binaries are not code-signed: Windows marks files that came out of a downloaded archive and
SmartScreen warns about them once, so the install clears that mark from the copy it makes.

By hand, on any OS with Claude Code:

```
claude plugin marketplace add <this folder>
claude plugin install net-android@net-android
```

or, for one session only, `claude --plugin-dir <this folder>`.

## Requirements

- .NET 10 runtime for the server; the .NET SDK with the `android` workload to build apps for
  profiling (`build_app`).
- Android platform-tools: `adb` on PATH, or `ANDROID_HOME` / `ANDROID_SDK_ROOT` set.
- `dotnet-dsrouter` ships in `tools/`; a global install is used when it is missing.

## What the skill is

`skills/net-android/SKILL.md` is the operating guide: preflight, which profiling mode answers
which question, how to read the numbers, debugging rules, driving the app's screen, and the
traps. Its reference files hold the details. Claude loads it on its own when a task matches its
description; `/net-android:net-android` loads it by hand.

## Layout

```
.claude-plugin/plugin.json      the plugin, with the server registration
.claude-plugin/marketplace.json this folder as a one-plugin marketplace
skills/net-android/             the skill and its references
bin/                            NetAndroid.Mcp.exe (the server) and its dependencies
tools/                          dotnet-dsrouter
build/                          MSBuild targets for build-time weaving
gui/                            NapGui.exe (Windows)
```
