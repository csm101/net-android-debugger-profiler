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
- Tooling TFM: net8.0. Repo layout: src/ (Core, Mcp), tests/, ThirdParty/
  (vendored upstream), DevTools/ (argv-driven probes), TestTarget/ (minimal
  net-android app, created in M0).

## Architecture status

Solution scaffold only. No engine code yet. See ARCHITECTURE.md.

## Milestones

- M0 - Spike (current): vendored debugger-libs building; TestTarget app;
  manual end-to-end attach on emulator pixel_7_-_api_33_0; console spike that
  connects, sets a breakpoint, hits it, reads a local. Resolves U1-U3.
- M1 - Engine + minimal MCP: DebugSession facade, AndroidLauncher,
  breakpoints, stepping, stack, locals; MCP tools for the same; integration
  test harness (deploy TestTarget, drive session, assert).
- M2 - Inspection depth: evaluate, object/array expansion, exception filters,
  threads, logcat capture, compact debug snapshot.
- M3 - the reference application hardening: attach to the real app, multi-assembly, source
  resolution against the the reference application checkout, device (non-emulator) support.
- M4 - Optional DAP frontend + packaging/registration (mirror the Delphi
  project's installer / register-mcp.ps1 approach).

## Target MCP tool surface (mirror of delphi-win64-debugger, adapted)

Same names/semantics where they carry over: attach_to_app (replaces
attach_to_process), launch_app (launch_debuggee; wraps the msbuild Run
target), list_devices + select_device (new), continue_and_wait,
wait_until_stopped, pause_execution, step_into/over/out, set_breakpoint(s),
list_breakpoints, remove_all_breakpoints, set_exception_filters,
get_call_stack, get_threads, get_locals, get_variable, expand_variable,
evaluate_expression, get_current_source_location, get_source_files,
get_loaded_assemblies (was get_loaded_modules), get_app_output (logcat;
replaces get_debuggee_output), get_debugger_output, get_exception_details,
get_debug_session_status, get_compact_debug_snapshot, detach_debugger,
stop_debugging, terminate_app. Memory read/write and raw stack scan: not
applicable to SDB - dropped unless a need appears.

## Stable commands

    dotnet build C:\GitHub\net-android-debugger\NetAndroidDebugger.slnx
    dotnet test  C:\GitHub\net-android-debugger\NetAndroidDebugger.slnx

## Important discoveries

(record here as they land; environment facts live in ANDROID_ATTACH_NOTES.md)
