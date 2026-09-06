This file provides guidance to Claude Code when working in this repository.

# What this repository is

One repository, two products for **.NET for Android** applications (C# on MonoVM),
kept together so that their device layer can be shared.

| Component | Where | What |
|---|---|---|
| **net-android-debugger** | `src/NetAndroidDebugger.Core` (engine), `.Mcp` (MCP server), `.Dap` (Debug Adapter Protocol adapter), `.Shared` (sources linked into both frontends), `ThirdParty/debugger-libs` (submodule), `tests/NetAndroidDebugger.Tests`, `TestTarget/Debugger`, `vscode/` | A debugger over the Mono Soft Debugger protocol, driven by agents through MCP and by editors through DAP |
| **net-android-profiler** | `src/NetAndroidProfiler.Core` (engine), `.Mcp`, `.Cli` (`nap`: control service and one-shot commands), `.Weave` (`nap-weave`), `.Collector`, `tests/NetAndroidProfiler.Tests`, `tests/WeaveSample`, `TestTarget/Profiler` with `TestTarget/TestTarget.Support`, `gui/` (Delphi + DevExpress GUI), `build/` (packaging, weaving targets), `examples/` (ProfileMeExample, the GUI tutorial app) | A CPU sampling, memory and instrumenting profiler over EventPipe and IL weaving, with MCP, CLI and GUI frontends |
| shared | `src/NetAndroid.Device` (the device layer both Cores use) with `tests/NetAndroid.Device.Tests`, `src/NetAndroid.Mcp` (the unified MCP server `net-android` over both product servers) with `tests/NetAndroid.Mcp.Tests` and `register-mcp.cmd`, `DevTools/` (probes of both, `scripts/` shared), `docs/` (root documents, `docs/debugger/`, `docs/profiler/`), the solutions `NetAndroidDebuggerProfiler.slnx` (everything), `NetAndroidDebugger.slnx`, `NetAndroidProfiler.slnx` | The shared device library and the unified MCP server, both done: `docs/ARCHITECTURE.md` |

Each product keeps a `CLAUDE.md` next to its Core with what is specific to it:
`src/NetAndroidDebugger.Core/CLAUDE.md` and `src/NetAndroidProfiler.Core/CLAUDE.md`.
Read the one for the component you work on. Namespaces, assembly names, publish
folders (`%LOCALAPPDATA%\net-android-debugger`, `%LOCALAPPDATA%\net-android-profiler`)
and MCP registration names are those of the two original repositories and do not change.

The ultimate real-world target of both products is **the reference application**
(`C:\Work\ReferenceApp\the reference application.sln`, project `App.Droid`, `net9.0-android35.0`,
`ApplicationId=App.Droid`). Architectural template of both: the Delphi Win64 debugger
project (`C:\Athens\GitHub\delphi-visual-studio-code-debugger`) - the same
frontends-over-one-core layout, living-documents methodology and
TDD-with-integration-tests discipline.

# Operating mode

Use minimum tokens.

Be concise. No filler. No praise. No motivational tone. No repeating the
request. No long plans unless asked. No unnecessary explanations.

If code requested: output code first.
If uncertain: say uncertain.
If idea is bad: say so directly.

Prefer practical solutions over academic ones.

# Critical token rules

Assume context window and usage budget are scarce.

Avoid re-reading many files unless necessary. Avoid broad repo scans. Inspect
only files relevant to the current task. Do not restate known project context.
Prefer direct edits over discussion.

When multiple approaches exist: choose the smallest production-relevant step.

# Prototype vs production

The TestTarget apps, the example app and the reference application are validation vehicles, not the
spec. Never design features around their specifics.

Not allowed:
- hardcoded package names, activity names, source paths, symbol or method names
- assumptions about one assembly, one thread, one AppDomain, one device, one trace size
- solutions that only work because a test target is trivial

If a temporary shortcut is unavoidable:
- mark it `// TODO PROTOTYPE`
- document what must change for real apps
- update the component's TASK_RESUME.md with the limitation

# Architecture rules shared by both products (blocking)

- Each engine lives in its Core (`src/NetAndroidDebugger.Core`,
  `src/NetAndroidProfiler.Core`) and is **frontend-neutral**: no MCP types, no DAP
  types, no GUI types, no JSON serialization or JSON-RPC in a Core. Frontends translate.
- The MCP servers, the DAP adapter, the CLI and the GUI are thin frontends over their
  Core's session facade. Any logic useful to more than one frontend belongs in Core.
- Device access (adb, device state, screen, the app's environment on the device) goes
  only through `src/NetAndroid.Device`, shared by both Cores: no second adb client, no
  `Process.Start` of adb, no direct `setprop` anywhere else. `docs/ARCHITECTURE.md` says
  what is in it; `docs/TEST_CATALOG.md` what its tests cover.
- **Licensing (blocking):** never reuse code, binaries or protocol adapters from
  proprietary tooling (the C# Dev Kit / .NET MAUI VS Code extension, the Visual Studio
  profiler). MIT sources are fine and are the reference implementations to read; each
  component's CLAUDE.md names its own.

# Session continuity rules

State lives at three levels:

- root `TASK_RESUME.md`: repository-level tasks (restructurings, the shared device
  library, the unified MCP server) and which component the current work belongs to;
- `docs/<component>/PROJECT_STATE.md`: that product's high-level permanent state;
- `docs/<component>/TASK_RESUME.md`: that product's exact current task state.

Update the TASK_RESUME.md you are working under continuously enough that work can
resume after an interruption in the middle of a long step. Do not wait for a full
step to finish. Update it after: a non-trivial discovery, a code edit, a test/build
result, a change of hypothesis, a switch of file/symbol/investigation path, or any
sign the token budget may run out.

If usage seems near limit: stop coding and update TASK_RESUME.md first.

TASK_RESUME.md must contain: current task, current substep, files/symbols in focus,
last completed action, next action if interrupted right now, what works, what is
failing, last test result, exact next step, traps/hypotheses.

PROJECT_STATE.md must contain: architecture status, implemented features, open
milestones, important technical discoveries, stable build/run commands. It must
not duplicate transient task state.

# Living specifications

Per component, under `docs/debugger/` and `docs/profiler/`:

- `ARCHITECTURE.md` - modules, session model, frontend contracts (plus the vendoring
  status of ThirdParty for the debugger, the SQLite schema contract for the profiler).
- `ANDROID_ATTACH_NOTES.md` (debugger) / `ANDROID_PROFILING_NOTES.md` (profiler) -
  everything empirically known about the device side of that product.
- `KNOWN_UNKNOWNS.md` - open questions that block or condition the work.
- `TEST_CATALOG.md` - what the suite covers, gaps as named tests.

At the root: `docs/ARCHITECTURE.md` (how the components relate, the shared device
layer, the unified MCP server as tracked decisions) and `docs/KNOWN_UNKNOWNS.md`
(questions that span both products).

Rules:
- Before investigating anything covered by a living spec: read it first. Do not
  re-derive what is already written.
- When you discover or confirm a fact: update the owning document in the same
  change set as the code or experiment that produced it.
- When an entry in a KNOWN_UNKNOWNS.md is resolved: move the answer into the
  owning document and delete the entry.
- If a document disagrees with the code: the code wins. Correct the document.

# Resume behavior

When starting a new session:

1. Read the root TASK_RESUME.md
2. Read the component's PROJECT_STATE.md and TASK_RESUME.md
3. Read the living specs relevant to the next step; always the component's
   KNOWN_UNKNOWNS.md and docs/KNOWN_UNKNOWNS.md
4. Inspect only referenced files first
5. Resume exactly from next step

Do not restart analysis from zero unless required. The `resume-session` skill
walks through exactly this.

# Development methodology: TDD with integration tests

- Each product's integration suite drives its real engine against its real
  TestTarget on the Android emulator or an attached device. This is the primary
  safety net - same philosophy as the Delphi project's DebuggerTests. The profiler's
  parsing and analysis layers also get fast tests against checked-in recorded traces.
- Before fixing any bug: add a failing test that reproduces it.
- Every brainstormed edge case becomes a **named test**, never prose:
  green tests are plain `[Fact]`; real known bugs are
  `[Fact(Skip = "TODO-RED: <root cause>")]`; not-yet-feasible cases are stubs
  whose body is `Assert.Fail("not implemented")` so removing Skip forces a real
  implementation.
- Update the component's TEST_CATALOG.md in the same change set as the test or fix.
- Run the suite of the component you changed after every change to its Core or
  frontends; after a change to the shared device library, both suites (delegate to
  the `test-runner` agent).

# Build and test

```powershell
dotnet build NetAndroidDebuggerProfiler.slnx     # everything, the example app included
dotnet build NetAndroidDebugger.slnx             # the debugger only (daily work, fast tests)
dotnet build NetAndroidProfiler.slnx             # the profiler only, the example app included
dotnet test  NetAndroidDebugger.slnx
dotnet test  NetAndroidProfiler.slnx
```

Environment facts (this machine): .NET SDK 10.0.400 with the `android` workload (the
MAUI example builds with it); adb at
`C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe`, not on PATH (both
products locate it themselves); emulator AVD `pixel_7_-_api_30` running as
`emulator-5554` (`AVD=pixel_7_-_api_30 SERIAL=emulator-5554 bash DevTools/scripts/ensure-emulator.sh`
starts it); a Xiaomi Redmi Note 8 Pro (`a-physical-device`) when plugged in; no desktop
Mono runtime. With more than one device attached, set `NAD_DEVICE_SERIAL` (the debugger
and its suite) and `NAP_TEST_SERIAL` (the profiler's device tests).

# Code generation rules

C# (`net10.0` for tooling; the TestTargets and the example follow the Android TFM they need):

- standard .NET conventions: 4-space indent, file-scoped namespaces,
  `nullable enable`, PascalCase public / camelCase locals
- prefer records and immutable data for protocol, session and analysis snapshots
- no `async void` (except event handlers); always flow `CancellationToken`
  through engine waits and long-running collection/analysis operations
- typed exceptions; never swallow exceptions silently - log and rethrow or
  surface through the session state
- XML doc comments on public Core API
- correctness over cleverness; log failures clearly; keep state transitions
  understandable; avoid hidden magic; boring robust code
- ThirdParty/ is upstream code: do not reformat it, do not "improve" it

# File encodings and line endings (blocking)

An existing file's encoding and line endings are never changed as a side effect
of editing its content, not even when the edit adds only ASCII. This rule lives
with the repository on purpose: it has to hold on every machine and for everyone
working here, not only where the code was first written.

- Determine the encoding before editing rather than assuming it from the
  extension: `head -c3 <path> | od -An -tx1` shows a BOM (`ef bb bf`) if there is
  one.
- A file that has a BOM keeps it. The BOM is not a leftover to clean up, it is
  what declares the encoding. Tools that claim to write "UTF-8" frequently write
  it without one and strip an existing one silently, so verify afterwards rather
  than trusting the tool.
- Sources here are UTF-8 without BOM, except where a file already carries one
  (several files of the example app do). A new file matches its siblings.
- Line endings are pinned in `.gitattributes`: `.sh` is LF; `.cmd`, `.bat`, `.ps1`
  and the Delphi sources (`.pas`, `.dpr`, `.dfm`, `.dproj`, `.inc`) are CRLF;
  everything else follows `text=auto`, which on this machine (`core.autocrlf=true`)
  means CRLF in the working tree and LF in the repository. Do not convert endings
  by hand, and never as part of a change that is about something else.
- `sed` and most Unix tools emit LF and strip `\r`, and several editors' write
  paths do the same. On a CRLF file, edit through PowerShell, or rewrite the file
  with the endings it already had.
- Verify after the edit, not before. A diff that marks every line as changed
  means the endings moved, and that has to be undone before committing.

If a file in another encoding ever enters this repository, the same rule governs
it: read and write it in its own encoding, and never let a content edit
transcode it.

# Documentation rules

For generated files, comments, README, docs, commit messages: normal
professional technical English. Do not use caveman style. Concise but polished.
Commit messages: one imperative line saying why.

# Response format after edits

After coding work, respond with only: files changed, what changed, build/test
executed, result, next step.

# Licensing and IP (blocking)

- This is proprietary, closed-source software.
  Copyright (c) 2026 MCA Software s.a.s. di Sirna Carlo & C. All rights
  reserved. The repository is private and must stay private: never publish
  sources, never add an open-source license.
- Dependency policy: MIT / BSD / Apache-2.0 only. GPL is forbidden (viral
  copyleft); LGPL only with explicit user approval and dynamic linking. The
  profiler's CLAUDE.md adds the MPL clause its GUI relies on.
- Every distributed release ships `THIRD-PARTY-NOTICES.txt` (at the root, one part
  per product) with the license texts of all embedded components. Both test suites
  fail when a distributed package or assembly is missing from it.
