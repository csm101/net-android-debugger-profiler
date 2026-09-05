# Project state

High-level permanent state. Transient task state lives in TASK_RESUME.md.

## What this is

MCP (first) and DAP (later, optional) debugger for .NET for Android apps,
built as thin frontends over a frontend-neutral engine library
(NetAndroidDebugger.Core) that wraps the Mono Soft Debugger client libraries
(mono/debugger-libs). Architecture cloned from the working Delphi Win64
debugger (C:\Athens\GitHub\delphi-visual-studio-code-debugger): same
two-frontends-over-one-JSON-free-core layout, same living-docs methodology,
same TDD-with-integration-tests discipline.

Ultimate real target: the reference application (C:\Work\ReferenceApp, net9.0-android35.0).

## Decisions taken (2026-08-20)

- Native engine on vendored mono/debugger-libs - NOT a bridge over an existing
  DAP adapter. Rationale: full control of the tool surface (rich
  snapshot/expansion tools like the Delphi MCP), no dependency on the stale
  vscode-mono-debug adapter, DAP frontend stays possible later over the same
  core. vscode-mono-debug remains a reference implementation.
- Official NuGet packages of debugger-libs are stale (2017): vendor sources
  (submodule or pruned copy - see KNOWN_UNKNOWNS U1).
- Proprietary C# Dev Kit / MAUI-extension adapter: excluded (license).
- MCP frontend on the official `ModelContextProtocol` NuGet SDK (2.2.0, MIT,
  stdio transport, attribute-based tools); hand-rolled JSON-RPC not needed.
- Attach is always "restart with agent" and the engine owns it (property,
  forwards, `am start`); msbuild only deploys. Multi-process via port rotation.
- Tooling TFM: net10.0. Repo layout: src/ (Core, Mcp), tests/, ThirdParty/
  (vendored upstream), DevTools/ (argv-driven probes), TestTarget/ (minimal
  net-android app, created in M0).

- Licensing (2026-08-20): proprietary closed source, copyright MCA Software
  s.a.s. di Sirna Carlo & C.; commercialization kept open; dependency policy
  MIT/BSD/Apache-2.0 only, no GPL.
- THIRD-PARTY-NOTICES.txt (2026-08-23): written from what is actually published,
  with each component's copyright read out of the shipped assemblies rather than
  from memory. Seven components: mono/debugger-libs, Mono.Cecil, Roslyn, the
  Microsoft.Extensions/.NET libraries, SymbolStore+FileFormats, Json.NET (all
  MIT) and the MCP C# SDK (**Apache-2.0**, not MIT as assumed - allowed by the
  policy, and it ships no NOTICE file to propagate). `register-mcp.cmd` copies
  the file next to the binaries, and a test fails if a shipped assembly is not
  named in it.

## Architecture status

Engine (`NetAndroidDebugger.Core`) plus two frontends over it: the MCP stdio
server and the DAP adapter. debugger-libs is vendored as a submodule and builds
on net10.0 against a fork carrying one patch (see ARCHITECTURE.md). TestTarget
is the debuggee for the suite; DevTools holds the probes and the editor
integration.

## Milestones

- M0 - Spike: DONE 2026-08-20. Vendored debugger-libs build; TestTarget app;
  end-to-end attach on emulator pixel_7_-_api_33_0 proven by SdbProbe
  (connect, breakpoint resolved + hit, threads/backtrace/locals, continue,
  detach). Resolved U1-U3; facts in ANDROID_ATTACH_NOTES.md / ARCHITECTURE.md.
- M1 - Engine + MCP: DONE 2026-08-21. DebugSession facade over N per-process
  SoftDebuggerSessions, AndroidLauncher with port rotation, breakpoints,
  stepping, stack, locals, evaluate, expansion; MCP stdio server on the official
  ModelContextProtocol SDK 2.2.0; `register-mcp.cmd` publishes and registers it.
- M2 - Inspection depth: DONE 2026-08-21. Evaluate, object/array/dictionary
  expansion, IEnumerable elements, exception filters, threads, structured logcat
  capture with filters, compact snapshot, evaluation options (timeouts,
  invoke-free safe mode), source-path discovery (`get_source_files`).
- M3 - the reference application hardening: DONE for the emulator, 2026-08-21. Attach, automatic
  attach of every process of the app - including `the app's own android:process`, whose
  `android:process` name has nothing to do with the package and which Visual
  Studio cannot debug at all - breakpoints on startup code and in App.Core,
  breakpoints on the periodic sync threads with deep expansion, stepping through
  async frames, exception filters on third-party assemblies, clean terminate.
  Remaining: a physical device over `adb connect` (U9).
- M4 - DAP frontend and packaging: DONE 2026-08-21. `NetAndroidDebugger.Dap`, a
  stdio adapter over the same DebugSession with a hand-rolled wire protocol;
  `register-mcp.cmd` publishes both frontends; `DevTools/vscode/DAP_CLIENTS.md`
  documents the client contract; `DevTools/vscode/net-android-debugger` is a
  VS Code extension contributing the `net-android` debug type (offline checks
  only - never yet run inside a real VS Code), installed by
  `install-vscode-extension.cmd` as a junction into the user's extensions
  folder. `DevTools/DapSmoke` drives the adapter against any installed app.

Open, both needing hardware: attach over `adb connect` and a mid-run debugger
disconnect (U9). See KNOWN_UNKNOWNS.md for the rest.

## Target MCP tool surface (mirror of delphi-win64-debugger, adapted)

Same names/semantics where they carry over: attach_to_app (replaces
attach_to_process), launch_app (launch_debuggee; wraps the msbuild Run
target), list_devices + select_device (new), continue_and_wait,
wait_until_stopped, pause_execution, step_into/over/out, set_breakpoint(s),
list_breakpoints, remove_all_breakpoints, set_exception_filters,
get_call_stack, get_threads, get_locals, get_variable, expand_variable,
evaluate_expression, get_current_source_location,
get_loaded_assemblies (was get_loaded_modules), get_source_files (resolves a
file name to the path compiled into the PDB), get_app_output (logcat;
replaces get_debuggee_output), get_debugger_output, get_exception_details,
get_debug_session_status, get_compact_debug_snapshot, detach_debugger,
stop_debugging, terminate_app. Memory read/write and raw stack scan: not
applicable to SDB - dropped unless a need appears. `select_device` was dropped
too: every tool that needs a device takes the serial explicitly.

Beyond the mirror: set_exception_rules / get_exception_rules /
use_global_exception_rules (the per-exception engine and its shared file), and
launch_from_config, which reads the launch parameters and the app's exception
rules from a VS Code `launch.json` — the same file, and the same field names,
the DAP frontend reads.

Also list_app_projects, the counterpart of list_devices for choosing which app
to launch, and the deduction behind it: launch_app reads the package name from
the project rather than having it restated.

Screen tools (2026-09-05), so the agent can bring the app to the point worth
debugging by itself: check_device_control, capture_screenshot (returns an image
block; works while the app is stopped), get_ui_hierarchy, tap_screen (by
coordinates or by a selector that must match exactly one view), swipe_screen,
press_key, type_text (ASCII), wake_screen. All through adb, no agent on the
device. The device defaults to the session's. Input injection is the one part a
vendor may gate (MIUI: "USB debugging (Security settings)"); debugging never
depends on it, and the refusal names the switch. README section 5 is the
per-vendor table.

## Stable commands

    dotnet build C:\GitHub\net-android-debugger\NetAndroidDebugger.slnx
    dotnet test  C:\GitHub\net-android-debugger\NetAndroidDebugger.slnx

## Important discoveries

(environment and protocol facts live in ANDROID_ATTACH_NOTES.md)

- Attach = `debug.mono.extra=debug=127.0.0.1:PORT,timeout=<device unix secs>,loglevel=N,server=y`
  + process (re)start + `adb forward` + `SoftDebuggerConnectArgs`. App listens,
  host connects. No late attach to a running process. Agent waits 30 s, then
  the process dies (and Android respawns it while the deadline is fresh).
- Detach kills the app (Mono agent without keepalive). Model detach == terminate.
- The msbuild `Run` target's own attach wiring is unreliable here (expired
  deadline); the engine owns the attach dance, msbuild only deploys.
- Consumers of the vendored libs must reference Mono.Cecil 0.10.1 explicitly.
- Emulator-based integration tests are viable: ~8 s per restart+connect+hit.
- Debuggee invocation is the fragile part of inspection: expanding a value
  invokes its getters, an invocation that outlasts the timeout is aborted (the
  member reads as an error, and the process may or may not survive), and
  unattended emulators must be headless with a hardware GPU.
- **Breakpoints are disarmed for the duration of any evaluation**: Mono resumes
  all threads during an invocation, so a breakpoint hit meanwhile freezes it
  for good. This was the single biggest source of instability and matters most
  for apps with periodic work (timers, sync services).
- All timestamps the session reports are on the **device** wall clock: logcat
  stamps device local time, and output arriving through SDB is shifted by the
  offset measured once per launch (`AndroidLauncher.DeviceClockOffset`).
- Details in ANDROID_ATTACH_NOTES.md / ARCHITECTURE.md; open questions in
  KNOWN_UNKNOWNS.md (U13 hit-count baseline is the notable one).
- **`logcat -c` does not clear on Android 11** (emulator API 30, 2026-09-05):
  the buffer stays readable and a launch used to attach to the previous
  session's dead pid. The stream now starts at the buffer's own newest stamp
  plus one millisecond (`-T`), which is exact where the device clock is not.
- Multi-process apps (the reference application spawns `:crash_report_process` at init): every
  process reads the same property; same port → helper dies in a respawn loop.
  Port rotation (rewrite the property right after the main process has read
  it, one SDB session per process) is verified and is the engine's model:
  a session is a set of per-process sub-sessions. End of session = clear
  property + force-stop package.
