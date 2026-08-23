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

## Native AOT (measured, working for nap)

`dotnet publish src\NetAndroidProfiler.Cli -c Release -r win-x64 -p:NapAot=true` produces a
single native `nap.exe`. It runs a real profiling session end to end - device, dsrouter,
EventPipe, TraceEvent analysis, SQLite - and `nap serve` answers /health and /devices, so
the GUI's control service works natively too.

Measured on 2026-08-23, win-x64:

| | framework-dependent | native AOT |
|---|---|---|
| size | 8.4 MB (bin\, 68 files) | 21 MB (one file) |
| `nap version` | 66 ms | 37 ms |
| sampling session on the emulator | works | works, same database (195 methods, 236 tree nodes) |

Three things had to be true, and none of them is optional:

1. **The build machine needs the Visual C++ tools.** Components
   `Microsoft.VisualStudio.Component.VC.Tools.x86.x64` and
   `Microsoft.VisualStudio.Component.Windows11SDK.26100`. Traps met on the way: the VS 18
   installer CLI has no `--wait` (exit 87, with the reason only in
   `%TEMP%\dd_installer_*.log`), and the first command that succeeds may be swallowed by
   the installer updating itself - the log says "Installer self-update complete", nothing
   is installed, and the command has to be issued again. `vswhere.exe` must also be on
   PATH when publishing, or ILCompiler's link step fails with a mangled command line
   naming link.exe and exit code 123.

2. **No reflection-based JSON.** Native AOT disables it outright, and the first call throws
   "Reflection-based serialization has been disabled for this application". The session
   spec (`SessionJsonContext`), what nap prints (`NapJsonContext`) and the whole control
   service wire (`ControlJsonContext`) are source-generated. Anonymous types cannot be, so
   the two nap outputs that used them are records now.

3. **TraceEvent must be rooted against trimming.** Its FastSerialization builds objects
   from type names read out of the trace, which the trimmer cannot see: trimmed, the first
   trace conversion dies with `Unable to create an object of type
   TraceCodeAddresses+ILToNativeMap`. `<TrimmerRootAssembly>` for
   `Microsoft.Diagnostics.Tracing.TraceEvent` and `Microsoft.Diagnostics.FastSerialization`
   fixes it and costs about 7 MB - most of the difference in the table above.

Not done yet: the **MCP server** is still framework-dependent (its SDK and
Microsoft.Extensions.AI carry their own reflection; nobody has tried), and **heap mode**
was not verified under AOT because the emulator refused heap dumps that afternoon on the
framework-dependent build too. The package therefore still ships framework-dependent
binaries; `-p:NapAot=true` is a per-project opt-in that works for nap today.

## Obfuscation (deferred)

Only our own assemblies could be obfuscated - Mono.Cecil, TraceEvent and the rest must
ship unmodified, and MPL-1.1 components must stay unmodified by licence - which protects
the thin layer and leaves the analysis libraries legible. Native AOT is the stronger
answer for the parts that can take it: there is no IL left to read. Revisit if the
product ships to customers who ask for it.
