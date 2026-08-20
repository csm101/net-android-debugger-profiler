This file provides guidance to Claude Code when working in this repository.

# Project purpose

Build a debugger for **.NET for Android** applications (C# on MonoVM), exposed
through an **MCP frontend** first and, later, optionally a **DAP frontend** —
mirroring the architecture of the Delphi Win64 debugger project
(`C:\Athens\GitHub\delphi-visual-studio-code-debugger`).

The ultimate real-world target is **the reference application** (`C:\Work\ReferenceApp\the reference application.sln`,
project `App.Droid`, `net9.0-android35.0`, `ApplicationId=App.Droid`).
Any test target app exists only for testing; it may be extended whenever needed
to validate new debugger features.

Key technical fact (do not re-derive): C# code on Android is **invisible to
JDWP**. It is debugged through the **Mono Soft Debugger protocol (SDB)** — a TCP
agent inside the app's MonoVM, reached via `adb forward`. See
`ANDROID_ATTACH_NOTES.md`.

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

The test target app (TestTarget) and the reference application are validation vehicles, not the
spec. Never design features around their specifics.

Not allowed:
- hardcoded package names, activity names, source paths, symbol names
- assumptions about one assembly, one thread, one AppDomain, one device
- solutions that only work because the test target is trivial

If a temporary shortcut is unavoidable:
- mark it `// TODO PROTOTYPE`
- document what must change for real apps
- update TASK_RESUME.md with the limitation

# Architecture rules (blocking)

- The engine lives in `src/NetAndroidDebugger.Core` and is **frontend-neutral
  and JSON-free**: no MCP types, no DAP types, no JSON serialization in Core.
  Frontends translate.
- `NetAndroidDebugger.Mcp` (and a future `NetAndroidDebugger.Dap`) are thin
  frontends over the same `DebugSession` facade. Any logic useful to both
  belongs in Core.
- Third-party debugger libraries (`mono/debugger-libs`: `Mono.Debugger.Soft`,
  `Mono.Debugging`, `Mono.Debugging.Soft`) are consumed as vendored sources
  under `ThirdParty/` — treat them as **read-only upstream**; local patches must
  be documented in `ARCHITECTURE.md`.
- **Licensing (blocking):** never reuse code, binaries, or protocol adapters
  from the proprietary C# Dev Kit / .NET MAUI VS Code extension. MIT sources
  (`mono/debugger-libs`, `microsoft/vscode-mono-debug`, `dotnet/android`) are
  fine and are the reference implementations to read.

# Session continuity rules

Maintain these files:

- PROJECT_STATE.md   (high-level permanent state)
- TASK_RESUME.md     (exact current task state)

Update TASK_RESUME.md continuously enough that work can resume after an
interruption in the middle of a long step. Do not wait for a full step to
finish. Update it after: a non-trivial discovery, a code edit, a test/build
result, a change of hypothesis, a switch of file/symbol/investigation path, or
any sign the token budget may run out.

If usage seems near limit: stop coding and update TASK_RESUME.md first.

TASK_RESUME.md must contain: current task, current substep, files/symbols in
focus, last completed action, next action if interrupted right now, what works,
what is failing, last test result, exact next step, traps/hypotheses.

PROJECT_STATE.md must contain: architecture status, implemented features, open
milestones, important technical discoveries, stable build/run commands. It must
not duplicate transient task state.

# Living specifications

- `ARCHITECTURE.md` — modules, threading model, session state machine, frontend
  contracts, vendoring status of ThirdParty.
- `ANDROID_ATTACH_NOTES.md` — everything empirically known about deploying,
  launching and attaching to a .NET Android app (msbuild properties, adb,
  sysprops, SDB handshake, quirks per device/emulator).
- `KNOWN_UNKNOWNS.md` — open questions that block or condition the work.
- `TEST_CATALOG.md` — what the suite covers, gaps as named tests.

Rules:
- Before investigating anything covered by a living spec: read it first. Do not
  re-derive what is already written.
- When you discover or confirm a fact: update the owning document in the same
  change set as the code or experiment that produced it.
- When an entry in KNOWN_UNKNOWNS.md is resolved: move the answer into the
  owning document and delete the entry.
- If a document disagrees with the code: the code wins. Correct the document.

# Resume behavior

When starting a new session:

1. Read PROJECT_STATE.md
2. Read TASK_RESUME.md
3. Read the living specs relevant to the next step; always KNOWN_UNKNOWNS.md
4. Inspect only referenced files first
5. Resume exactly from next step

Do not restart analysis from zero unless required.

# Development methodology: TDD with integration tests

- The integration suite in `tests/NetAndroidDebugger.Tests` drives the real
  engine against a real debuggee (TestTarget app on the Android emulator
  `pixel_7_-_api_33_0` or an attached device). This is the primary safety net —
  same philosophy as the Delphi project's DebuggerTests.
- Before fixing any bug: add a failing test that reproduces it.
- Every brainstormed edge case becomes a **named test**, never prose:
  green tests are plain `[Fact]`; real known bugs are
  `[Fact(Skip = "TODO-RED: <root cause>")]`; not-yet-feasible cases are stubs
  whose body is `Assert.Fail("not implemented")` so removing Skip forces a real
  implementation.
- Update TEST_CATALOG.md in the same change set as the test or fix.
- Run the full suite after every change to Core or the MCP frontend
  (delegate to the `test-runner` agent).

# Build and test

```powershell
dotnet build C:\GitHub\net-android-debugger\NetAndroidDebugger.slnx
dotnet test  C:\GitHub\net-android-debugger\NetAndroidDebugger.slnx
```

Environment facts (this machine): .NET SDK 10.0.301 with `android` workload;
adb at `C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe`
(on PATH); emulator AVD `pixel_7_-_api_33_0`; no desktop Mono runtime.

# Code generation rules

C# (`net8.0` for tooling; TestTarget follows the Android TFM it needs):

- standard .NET conventions: 4-space indent, file-scoped namespaces,
  `nullable enable`, PascalCase public / camelCase locals
- prefer records and immutable data for protocol/session snapshots
- no `async void` (except event handlers), always flow `CancellationToken`
  through engine waits
- typed exceptions; never swallow exceptions silently — log and rethrow or
  surface through the session state
- XML doc comments on public Core API
- correctness over cleverness; log failures clearly; keep state transitions
  understandable; avoid hidden magic; boring robust code
- ThirdParty/ is upstream code: do not reformat it, do not "improve" it

# Documentation rules

For generated files, comments, README, docs, commit messages: normal
professional technical English. Do not use caveman style. Concise but polished.

# Response format after edits

After coding work, respond with only: files changed, what changed, build/test
executed, result, next step.
