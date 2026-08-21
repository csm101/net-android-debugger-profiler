# Task resume

## Current task
P4, the Delphi GUI. It is a working AQTime-shaped application: docking layout,
light/dark themes, saved layouts, settings, and the panels Report, Details,
Call tree, Call graph, Source, Memory, Monitor, Summary, Session log, Explorer.
This round added the toolbar, the tables' working tools and the checks that
cover them.

Landed today (each committed and pushed):
- Toolbar on `TdxBarManager` (bars Session and View) with glyphs drawn in code
  and tinted with the theme; the session status moved to a status bar.
- Live heap chart per snapshot; the Memory panel opens the page that has data.
- Sampling call tree and its critical path built on `inclusive_cpu` /
  `exclusive_cpu`: a path through a thread parked in a wait is not a bottleneck.
- Find panel (Ctrl+F) on every grid; export of the focused table to xlsx, csv,
  html or text, also available as `--export=<file>`; Ctrl+O and F5.
- Call graph: right-click walks back, double-click opens the code, arrows have
  heads, and a cut fan says how many boxes it dropped.
- Clear on the toolbar, and Snapshot/Pause/Clear greyed out unless the session
  runs on the weaver engine.
- gui/tests/smoke.ps1: starts the real window once per panel and per dialog
  against sessions of each mode and fails on a startup error log.
- gui/tests/ControlTests.dpr: drives a real device session through the same
  client the GUI uses.

## What the new checks found (all fixed)
- A run that died before reading its settings wrote an empty font name and a size
  of zero; the next run applied them and died the same way. Values are corrected
  on read and on write now.
- The Setup dialog could not name the assemblies to weave, and inference from the
  callspec is wrong whenever the namespace is deeper than the assembly
  (`N:TestTarget.Workloads` lives in `TestTarget.dll`). There is a field for it.
- A weaver session cleared and then stopped before new events arrived ended
  Failed with "No .napw event files". An empty result is the truth: the session
  now ends Ready with a warning (ProfilerSession.AnalyzeWeaverEventsAsync).
- Setting the height of a dock panel that had been wrapped into a tab container
  did nothing, so every call view opened as a 100px sliver.

## Next
P5 packaging: bundle dotnet-dsrouter with an optional global-tool fallback, a
register script that does not depend on repo paths, versioning across tool and
schema, SynEdit in THIRD-PARTY-NOTICES, and an AOT/obfuscation spike (TraceEvent's
reflection is the likely blocker).

## Substep
Waiting on the full .NET suite after the ProfilerSession change; the Delphi side
builds, StoreTests and ControlTests pass, smoke.ps1 passes on three sessions.

## Files in focus
gui/src/uMainForm.pas, uGlyphs.pas, uSetupDialog.pas, uControlClient.pas,
uSessionStore.pas, uSettings.pas, uLayouts.pas;
gui/tests/{smoke.ps1, ControlTests.dpr, StoreTests.dpr};
src/NetAndroidProfiler.Core/Sessions/ProfilerSession.cs.

## Next action if interrupted right now
Read the suite log at scratchpad/suite-gui2.log; if green, start P5.

## How to run what exists
    gui\build-gui.cmd                       builds NapGui.exe
    gui\tests\build-tests.cmd               builds StoreTests.exe and ControlTests.exe
    gui\StoreTests.exe <session.db> [...]   data-layer checks
    gui\ControlTests.exe <nap.exe> [serial] device path, end to end
    gui\tests\smoke.ps1 -Sessions @(...)    every panel and dialog of the real window

## Traps found here
- Evidence collected before a fix stays in the docs and looks authoritative:
  re-test a "known" blocker before investigating it.
- A GUI that writes its preferences on exit can poison its own next start; treat
  values read from disk as untrusted.
- `TScrollBox` does not publish `OnKeyDown`, and a `TPaintBox` cannot take focus:
  keyboard navigation inside a painted panel needs the form, not the control.
- Filling a `TdxBarCombo` raises the same change event a click does, while half
  the window does not exist yet.

## Queue (highest value first)
- P5 packaging (above).
- U10: physical devices - never tried, and `adb reverse` differs from the emulator.
- U4b: suspend choreography against the reference application's watchdogs.
- U19: symbol-server lookup for Desymbolicate.
- The MCP device tests still unchecked in TEST_CATALOG (profile_run,
  profile_start/profile_stop, profile_annotate_source).
