This file provides guidance to Claude Code when working in this repository.

# Project purpose

Build a **profiler** for .NET for Android applications (C# on MonoVM): CPU
sampling, memory/allocation analysis, and instrumenting (deterministic
enter/leave) profiling. Frontends: **MCP** first (agent-driven profiling),
then a **rich GUI in Delphi + DevExpress VCL** (AQTime-style). Free
replacement for the Visual Studio Enterprise Android profiler.

Sibling project and architectural template: C:\GitHub\net-android-debugger
(and, before it, C:\Athens\GitHub\delphi-visual-studio-code-debugger) - same
frontends-over-one-core layout, same living-docs methodology, same
TDD-with-integration-tests discipline. The adb/deploy orchestration overlaps
with the debugger project; extracting a shared library is a tracked decision,
not an accident (see KNOWN_UNKNOWNS).

The ultimate real-world target is **the reference application** (C:\Work\ReferenceApp\the reference application.sln,
project App.Droid, net9.0-android35.0, ApplicationId=App.Droid). Any test
target app exists only for testing; extend it whenever needed.

Key technical facts (do not re-derive; details in ANDROID_PROFILING_NOTES.md):
collection infrastructure already exists in the official toolchain
(EnableDiagnostics=true build, dotnet-trace collect --dsrouter android,
dotnet-gcdump, EventPipe provider Microsoft-DotNETRuntimeMonoProfiler for
enter/leave + allocations). The value of this project is orchestration,
analysis, MCP tools, and the GUI - not reinventing collection.

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

TestTarget and the reference application are validation vehicles, not the spec. Never design
features around their specifics.

Not allowed:
- hardcoded package names, activity names, source paths, method names
- assumptions about one assembly, one thread, one device, one trace size
- solutions that only work because the test target is trivial

If a temporary shortcut is unavoidable: mark it `// TODO PROTOTYPE`, document
what must change for real apps, update TASK_RESUME.md.

# Architecture rules (blocking)

- The engine lives in src/NetAndroidProfiler.Core and is **frontend-neutral**:
  no MCP types, no GUI types, no JSON-RPC in Core. Frontends translate.
- Analysis results land in a **SQLite database with a versioned, documented
  schema** (see ARCHITECTURE.md): it is the contract the Delphi GUI reads.
  Schema changes bump the version and update ARCHITECTURE.md in the same
  change set.
- NetAndroidProfiler.Mcp is a thin frontend over the Core session facade.
  The future Delphi GUI consumes SQLite directly plus a small local control
  service; any logic useful to more than one frontend belongs in Core.
- Trace parsing goes through the **TraceEvent** library
  (Microsoft.Diagnostics.Tracing.TraceEvent, MIT, NuGet - maintained; unlike
  the debugger project there is nothing to vendor).
- **Licensing (blocking):** never reuse code or binaries from the Visual
  Studio profiler or other proprietary tooling. MIT sources
  (dotnet/diagnostics tools, TraceEvent, Fody, Mono.Cecil,
  jonathanpeppers/Mono.Profiler.Android) are fine and are the references to
  read.

# Session continuity rules

Maintain these files:

- PROJECT_STATE.md   (high-level permanent state)
- TASK_RESUME.md     (exact current task state)

Update TASK_RESUME.md continuously enough that work can resume after an
interruption in the middle of a long step. Do not wait for a full step to
finish. Update it after: a non-trivial discovery, a code edit, a test/build
result, a change of hypothesis, a switch of file/symbol/investigation path,
or any sign the token budget may run out.

If usage seems near limit: stop coding and update TASK_RESUME.md first.

TASK_RESUME.md must contain: current task, current substep, files/symbols in
focus, last completed action, next action if interrupted right now, what
works, what is failing, last test result, exact next step, traps/hypotheses.

PROJECT_STATE.md must contain: architecture status, implemented features,
open milestones, important technical discoveries, stable build/run commands.
It must not duplicate transient task state.

# Living specifications

- ARCHITECTURE.md - modules, session model, SQLite schema contract, frontend
  contracts.
- ANDROID_PROFILING_NOTES.md - everything empirically known about collecting
  profiles from a .NET Android app (msbuild properties, dsrouter/dotnet-trace,
  MONO_DIAGNOSTICS, adb, suspend, output formats, overhead measurements,
  per-device quirks).
- KNOWN_UNKNOWNS.md - open questions that block or condition the work.
- TEST_CATALOG.md - what the suite covers, gaps as named tests.

Rules:
- Before investigating anything covered by a living spec: read it first. Do
  not re-derive what is already written.
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

- The integration suite in tests/NetAndroidProfiler.Tests drives the real
  pipeline: profile a real TestTarget run on the Android emulator
  (DevicePerSviluppoProfiler) or an attached device, then assert on the analyzed
  output (hotspots present, known method visible, allocation attributed).
  Parsing/analysis layers also get fast tests against **checked-in recorded
  trace files** - no device needed; keep them small.
- Before fixing any bug: add a failing test that reproduces it.
- Every brainstormed edge case becomes a **named test**, never prose: green
  tests are plain [Fact]; real known bugs are
  [Fact(Skip = "TODO-RED: <root cause>")]; not-yet-feasible cases are stubs
  whose body is Assert.Fail("not implemented") so removing Skip forces a real
  implementation.
- Update TEST_CATALOG.md in the same change set as the test or fix.
- Run the suite after every change to Core or the MCP frontend (delegate to
  the test-runner agent).

# Build and test

    dotnet build C:\GitHub\net-android-profiler\NetAndroidProfiler.slnx
    dotnet test  C:\GitHub\net-android-profiler\NetAndroidProfiler.slnx

Environment facts (this machine): .NET SDK 10.0.301 with android workload;
adb at C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe
(on PATH); emulator AVD DevicePerSviluppoProfiler; no desktop Mono runtime.

# Code generation rules

C# (net10.0 for tooling; TestTarget follows the Android TFM it needs):

- standard .NET conventions: 4-space indent, file-scoped namespaces,
  nullable enable, PascalCase public / camelCase locals
- prefer records and immutable data for analysis snapshots
- no async void (except event handlers); flow CancellationToken through
  long-running collection/analysis operations
- typed exceptions; never swallow exceptions silently - log and rethrow or
  surface through session state
- XML doc comments on public Core API
- profiling data paths are hot: measure before optimizing, but avoid obvious
  per-event allocations in parsers
- correctness over cleverness; boring robust code

Delphi GUI (when it starts, in gui/): follows the coding standards of the
VendingService workspace (C:\Athens\VendingService\AGENTS.md) - DevExpress
VCL, no with, typed exceptions, 2-space indent.

# Documentation rules

For generated files, comments, README, docs, commit messages: normal
professional technical English. Do not use caveman style. Concise but
polished.

# Response format after edits

After coding work, respond with only: files changed, what changed, build/test
executed, result, next step.

# Licensing and IP (blocking)

- This is proprietary, closed-source software.
  Copyright (c) 2026 MCA Software s.a.s. di Sirna Carlo & C. All rights
  reserved. The repository is private and must stay private: never publish
  sources, never add an open-source license.
- Dependency policy: MIT / BSD / Apache-2.0 only, plus MPL-1.1/2.0 used
  **unmodified** (its copyleft is per file, so linking it into a closed product
  is fine as long as we do not change its own sources - keep customization in our
  units). GPL is forbidden (viral copyleft); LGPL only with explicit user
  approval and dynamic linking. Approved under the MPL clause: SynEdit
  (C:\Athens\SynEdit, MPL-1.1/LGPL-2.1 dual) as the GUI's source editor.
- The GUI uses DevExpress VCL under its commercial license: never
  redistribute DevExpress sources or components.
- Every distributed release ships THIRD-PARTY-NOTICES.txt with the license texts
  of all embedded MIT/BSD/Apache/MPL components (TraceEvent, Mono.Cecil,
  SQLitePCLRaw, ModelContextProtocol, and SynEdit once the GUI ships). The file
  exists and Fast/ThirdPartyNoticesTests fails when a distributed package is
  missing from it.
