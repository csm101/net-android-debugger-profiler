# Packaging

What a release is, how it is built, and what a machine needs to run it.

    powershell -File build\package.ps1                 Release package into dist\
    powershell -File build\package.ps1 -SkipDsRouter   offline: leave tools\ empty
    powershell -File build\package.ps1 -SkipGui        do not look for the Delphi GUI

The script publishes, assembles and zips; it does not build the GUI, which needs
RAD Studio - run `gui\build-gui.cmd` first and the package picks the executable up.

## Version

`Directory.Build.props` holds `NapVersion`, and everything reads it: the assemblies,
`ProfilerSession.ToolVersion` (through the assembly's informational version), the
`schema_info.tool_version` stamp of every session database, `/health`, the MCP server's
handshake, and the name of the package. A release is one edit there.

The **schema** version is separate and lives in `ResultSchema` (currently 4). They move
independently on purpose: a tool release that does not change the database must not
invalidate databases, and a schema change is documented in ARCHITECTURE.md in the same
change set. `PackagingTests` fails if the tool version stops coming from the build.

## Layout

    bin\      the MCP server, nap.exe, nap-weave and their shared dependencies
    tools\    dotnet-dsrouter
    build\    NetAndroidProfiler.Weaving.targets and the nap-weave copy it runs
    gui\      NapGui.exe, when the package was built with it
    install.cmd, README.txt, LICENSE, THIRD-PARTY-NOTICES.txt

One `bin\` for the three .NET entry points: their dependency closures overlap almost
entirely, and one directory is one thing to put on a PATH.

`build\` is a second copy of nap-weave rather than a reference into `bin\`, because the
MSBuild targets resolve the tool as `tools\nap-weave.dll` next to the .targets file, and
an app's build imports that file by path. Two copies of a 160 KB tool is the cheaper
half of that trade.

## dotnet-dsrouter travels with the package

The profiler drives dsrouter as a process. Requiring `dotnet tool install -g
dotnet-dsrouter` made the install a two-step affair and left the version to chance, so
the package carries it and `ToolLocator` looks there **first**:

1. `NETANDROIDPROFILER_DOTNET_DSROUTER` (explicit override)
2. `<app>\tools\`, `<app>\`, `<app>\..\tools\` - the package's own copy
3. `%USERPROFILE%\.dotnet\tools\` - the global tool
4. PATH

An installation that carries its own dsrouter must not start behaving differently
because the machine happens to have another version installed globally; that is what
the order is for. `nap doctor` prints which one was chosen and whether it came from the
package. The tool store's `.nupkg` archives are deleted after the install: the shim needs
the extracted files, and the archive is a third of the package for nothing.

## The GUI inside the package

`NapGui.exe` sits in `gui\` and starts `nap serve` for the live controls, so it looks for
`nap.exe` next to itself, then in `..\bin\` - the package layout - then in the repository's
build output, and finally wherever Settings points. Keep the package together, or set the
path in Settings.

## Installing

`install.cmd` registers `bin\NetAndroidProfiler.Mcp.dll` with Claude Code at user scope,
pointing at wherever the package was unpacked - no repository, no publish step, and
`install.cmd /remove` undoes it. It warns when adb is missing and says which dsrouter
will be used.

`register-mcp.cmd` in the repository root is the development counterpart: it publishes
from source into `%LOCALAPPDATA%` and registers that. Same registration, different
source of truth.

## Native AOT and obfuscation (measured, not adopted)

Publishing `nap` with `PublishAot=true` on this machine:

- **Blocked on the build machine**: `error : Platform linker not found` - NativeAOT needs
  the Visual C++ Desktop Development workload. That turns a machine with the .NET SDK
  into a machine with Visual Studio build tools.
- **Blocked in the solution**: the property flows into `ProjectReference`s, and
  `NetAndroidProfiler.Collector` targets Android - `NETSDK1207: ahead-of-time compilation
  is not supported for the target framework`. AOT would have to be a per-project opt-in.
- **Warnings before linking**: every reflection-based `System.Text.Json` call raises
  IL2026/IL3050 (fixable with source generators), and TraceEvent - the trace reader the
  whole product rests on - is reflection-heavy and carries native symbol interop
  (Dia2Lib). It is not documented as AOT-compatible.

Attempted again once the toolchain question came up: the `PublishAot` property is now an
opt-in per project (`-p:NapAot=true` on nap and the MCP server), which settles the
NETSDK1207 half. The machine half is not settled: installing the components from the
command line fails at the elevation prompt, and the VS Installer reports
`Status changed to UpdateAvailable` - it wants to update itself before it will modify an
installation, and in `--passive` mode it exits silently instead of saying so. An
`MSVC\14.51.36231\link.exe` left by another workload is not enough on its own: without a
registered `VC.Tools.x86.x64` and a Windows SDK carrying its `Lib`/`Include`, ILCompiler
still reports "Platform linker not found".

To resume: open Visual Studio Installer, let it update itself, Modify the installation and
add **Desktop development with C++** (or the components `VC.Tools.x86.x64` and
`Windows11SDK.26100`). Then publish nap with `-p:NapAot=true -r win-x64` and run `nap run`
against a device: the analysis happens before the command prints anything, so that single
run answers the TraceEvent question even though the final JSON print will fail until the
serializers are source-generated.

Conclusion: AOT buys startup time and a single file, and costs a toolchain dependency
plus a port of the JSON layer and a bet on TraceEvent. Not now. The framework-dependent
package starts fast enough for a tool that then waits on a device.

Obfuscation is the same shape of decision and is likewise deferred: only our own
assemblies could be obfuscated (Mono.Cecil, TraceEvent and the rest must ship
unmodified, and MPL-1.1 components must stay unmodified by licence), which protects the
thin layer and leaves the analysis libraries legible. Revisit if the product ships to
customers who ask for it.
