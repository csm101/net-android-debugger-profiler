Guidance specific to the profiler component. The root `CLAUDE.md` holds the rules
shared by both products; read it first.

# Project purpose

Build a **profiler** for .NET for Android applications (C# on MonoVM): CPU
sampling, memory/allocation analysis, and instrumenting (deterministic
enter/leave) profiling. Frontends: **MCP** first (agent-driven profiling), the
**`nap` CLI and control service**, and a **rich GUI in Delphi + DevExpress VCL**
(AQTime-style). Free replacement for the Visual Studio Enterprise Android profiler.

Key technical facts (do not re-derive; details in
`docs/profiler/ANDROID_PROFILING_NOTES.md`): collection infrastructure already
exists in the official toolchain (EnableDiagnostics=true build, dotnet-trace
collect --dsrouter android, dotnet-gcdump, EventPipe provider
Microsoft-DotNETRuntimeMonoProfiler for enter/leave + allocations). The value of
this project is orchestration, analysis, MCP tools, and the GUI - not reinventing
collection.

# Architecture rules (blocking)

- The engine lives in `src/NetAndroidProfiler.Core` and is **frontend-neutral**:
  no MCP types, no GUI types, no JSON-RPC in Core. Frontends translate.
- Analysis results land in a **SQLite database with a versioned, documented
  schema** (see `docs/profiler/ARCHITECTURE.md`): it is the contract the Delphi
  GUI reads. Schema changes bump the version and update ARCHITECTURE.md in the
  same change set.
- `NetAndroidProfiler.Mcp` and `NetAndroidProfiler.Cli` (`nap`) are thin frontends
  over the Core session facade. The Delphi GUI consumes SQLite directly plus the
  local control service `nap serve`; any logic useful to more than one frontend
  belongs in Core.
- Device access - adb, the device's state, the app's override environment file,
  `debug.mono.profile` - goes only through `src/NetAndroid.Device` (`AdbClient`,
  `AdbLocator`, `AppEnvironment`, `DevicePropertyOverride`); `ToolLocator` keeps only
  dsrouter, dotnet and the weaving targets. No `Process.Start` of adb, no second client,
  no direct `setprop` in this Core or its frontends.
- Trace parsing goes through the **TraceEvent** library
  (Microsoft.Diagnostics.Tracing.TraceEvent, MIT, NuGet - maintained; unlike
  the debugger there is nothing to vendor).
- **Licensing (blocking):** never reuse code or binaries from the Visual
  Studio profiler or other proprietary tooling. MIT sources
  (dotnet/diagnostics tools, TraceEvent, Fody, Mono.Cecil,
  jonathanpeppers/Mono.Profiler.Android) are fine and are the references to
  read.

# Living specifications

Under `docs/profiler/`: `ARCHITECTURE.md` (modules, session model, SQLite schema
contract, frontend contracts), `ANDROID_PROFILING_NOTES.md` (everything
empirically known about collecting profiles from a .NET Android app: msbuild
properties, dsrouter/dotnet-trace, MONO_DIAGNOSTICS, adb, suspend, output formats,
overhead measurements, per-device quirks), `KNOWN_UNKNOWNS.md`, `TEST_CATALOG.md`,
`PROJECT_STATE.md`, `TASK_RESUME.md`, the user-facing `README.md` and the guides
`USAGE.md`, `APP_SETUP.md`, `GUI_DESIGN.md`, `CONTROL_SERVICE.md`, `PACKAGING.md`.

# Test suite

`tests/NetAndroidProfiler.Tests`: `Fast/` runs against checked-in recorded traces
(no device; keep them small); `Device/` (tagged `Category=Device`) profiles a real
run of `TestTarget/Profiler` (package `com.mcasoftware.testtarget`, installed with
`-p:EnableDiagnostics=true`) on the emulator or an attached device, then asserts on
the analyzed output. `NAP_TEST_SERIAL` names the device (default `emulator-5556`),
`NAP_TEST_PACKAGE` the package; the reference application tests are opt-in (`NAP_REFAPP=1`).

```powershell
dotnet build NetAndroidProfiler.slnx
dotnet test  NetAndroidProfiler.slnx --filter "Category!=Device"   # fast pass
dotnet test  NetAndroidProfiler.slnx                                # everything
```

# Frontends, tools and the example

`register-mcp-profiler.cmd` publishes the MCP server to
`%LOCALAPPDATA%\net-android-profiler` (override with `NAP_INSTALL_DIR`) and
registers it in Claude Code as `net-android-profiler`. `gui\build-gui.cmd` (or
`build-gui.cmd` at the root) builds `gui\NapGui.exe`; `build\package.ps1` builds the
redistributable package. `examples/ProfileMeExample.sln` is the tutorial app of the
GUI: one deliberate performance problem per screen, each screen with a Guide, the
per-screen table in `docs/profiler/README.md`; it is a shipped, first-class part
of the repository, not a test fixture.

# Code generation

Beyond the shared rules: prefer records and immutable data for analysis
snapshots; profiling data paths are hot - measure before optimizing, but avoid
obvious per-event allocations in parsers.

Delphi GUI (`gui/`): follows the coding standards of the VendingService
workspace (`C:\Athens\VendingService\AGENTS.md`) - DevExpress VCL, no `with`,
typed exceptions, 2-space indent, CRLF sources.

**Never a stock VCL control where DevExpress has the equivalent (blocking).** The
window is skinned, and a native control is a hole in it: it paints the system's
colours, its scrollbars ignore the theme, and on a dark skin it shows up as a white
rectangle. This is not a matter of taste - it is why the call graph flashed a white
sheet on every resize. Known equivalents, to be used without asking:

| stock VCL | use |
|---|---|
| `TPanel` | `TdxPanel` (dxPanel) |
| `TScrollBox` | `TcxScrollBox` (cxScrollBox) |
| `TSplitter` | `TcxSplitter` (cxSplitter) |
| `TPageControl` / `TTabSheet` | `TcxPageControl` / `TcxTabSheet` (cxPC) |
| `TLabel`, `TEdit`, `TComboBox`, `TCheckBox`, `TMemo`, `TButton`, `TListBox` | the `Tcx` ones |
| `MessageDlg`, `ShowMessage` | `dxMessageDlg` (dxMessageDialog) |
| `InputQuery`, `InputBox` | `dxInputQuery`, `dxInputBox` (dxInputDialogs) |
| `TStatusBar` | `TdxStatusBar` with `PaintStyle = stpsUseLookAndFeel` |
| toolbars, menus | `TdxBarManager` (and see the no-floating-bars rule in GUI_DESIGN.md) |

What legitimately stays stock, because DevExpress has nothing for it: `TPaintBox`
(our own drawing), `TTimer`, `TOpenDialog`/`TSaveDialog` and `SelectDirectory` (the
shell's own dialogs, which are the ones users expect), `TForm` itself, and anything
under `Winapi`. When something new has no equivalent, say so in the code comment
rather than leaving the reader to wonder whether it was an oversight.

# Licensing additions

- MPL-1.1/2.0 components may be used **unmodified** (its copyleft is per file, so a
  larger work under other terms is fine as long as we do not change their own sources
  - keep customization in our units). Approved under this clause: SynEdit
  (`C:\Athens\SynEdit`, MPL-1.1/LGPL-2.1 dual) as the GUI's source editor.
- The GUI uses DevExpress VCL under its commercial license: never redistribute
  DevExpress sources or components. The GUI's own sources are MIT like the rest, so
  say plainly wherever it matters that building them needs a DevExpress license;
  the released `NapGui.exe` is what somebody without one can use.
- `THIRD-PARTY-NOTICES.txt` (root, profiler part) lists TraceEvent, Mono.Cecil,
  SQLitePCLRaw, ModelContextProtocol, dotnet-dsrouter, SynEdit and JclDebug;
  `Fast/ThirdPartyNoticesTests` fails when a distributed package is missing from it.
