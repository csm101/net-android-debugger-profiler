# Task resume (repository level)

## Last repository-level task, done: the operating skill and the installation package (2026-09-08)

Follow-up 2b, decided with the user: one skill in English for anyone using the `net-android` server
with a .NET for Android or MAUI app (nothing of this repository, its scripts, its test apps or any
customer app may be assumed); shipped as a Claude Code plugin inside one installation package that
also carries the unified server, `nap`, dsrouter, the weaving targets and the Delphi GUI (distributed
only: driving the GUI from the server is a later phase); a `build_app` tool over Core's `AppBuilder`
so the skill never carries msbuild lines. Plan: `C:\Users\Carlo\.claude\plans\task-build-the-silly-rivest.md`.

Step 1 done: `BuildPurpose` (Core, Projects) + `build_app` in `ProfilerTools` (both servers see it);
`Fast/BuildPurposeTests` 8/8, `McpDeviceTests.Build_app_installs_the_test_target_ready_for_sampling`
green on emulator-5554; catalog and profiler ARCHITECTURE updated.
Steps 2-5 done: `plugin/` (`.claude-plugin/plugin.json` with the server inline, `marketplace.json`,
README), `build/package.ps1` extended into the `net-android-<version>` package (unified server in
`bin/`, GUI, plugin files at the root, version stamped into plugin.json), `install.cmd` rewritten
(plugin route with fallback, desktop shortcut, `/remove`), `README.txt` rewritten; the skill
(`SKILL.md` 195 lines + `references/` build-matrix, reading-results, ui-driving, traps);
`SkillTests` 6/6; package built (72.9 MB zip), `claude plugin validate` passes, the packaged server
answers the profiler's MCP tests; `docs/ARCHITECTURE.md` decision 3, README, root TEST_CATALOG G,
profiler PACKAGING. Not run on this machine: `install.cmd` (registers at user scope: the user's
call). Not done, later phase: driving the GUI from the server.

## Last repository-level task, done: profiling the app the debugger runs (2026-09-08)

Question from the user: can one session run under the debugger to a breakpoint and then profile
what follows? Answer, verified: yes, by keeping the debugger attached and attaching the sampling
profiler to the same process (Mono ends the app when the debugger detaches, so that road is closed).
Flow: breakpoint, `remove_all_breakpoints` + clear the exception rules, `profile_start` with
`launch: attach` on the same package and device, `continue_and_wait`, `profile_stop`, `profile_hotspots`.
The runtime connected to the profiler while still stopped at the breakpoint, so the samples start
with the code after it. Written up in `docs/profiler/ANDROID_PROFILING_NOTES.md` ("Profiling the app
the debugger runs"), `docs/ARCHITECTURE.md` decision 2, the server instructions, `docs/TEST_CATALOG.md` G.

Code: `DeviceArbiter.Refusal` takes the attach target and the debugged package (`DebugHold`); that
one start passes, every other profiler start on the debugger's device is still refused and the
refusal says how to attach. `tests/NetAndroid.Mcp.Tests`: `Support.cs`, four more arbiter tests,
`DebugAndProfileTogetherTests` (device). Suite: 19/19 on emulator-5554 (2026-09-08).

Found on the way and fixed the same day: a weaver session killed half-way leaves
`<assembly>.pdb.naporig` in the override directory and `WeaveDeployer` restored only the dll; the
debugger then bound no breakpoint. `RestoreLeftoversAsync` now puts the pdb back too, with
`WeaveDeployerTests.Deployer_restores_a_pdb_left_aside_by_a_killed_session` (red before, green after,
emulator-5554). Port 9000 was held by Docker (`sal-minio`) for one run; the user freed it.

## Previous repository-level task, done: the unified MCP server (`docs/ARCHITECTURE.md`, decision 2; 2026-09-06 evening)

No repository-level task is in progress. The next ones, in the recommended order, are the follow-ups
at the end of this file (the agent skill first).

One server, `net-android`, over both Cores. Design: a third Exe `src/NetAndroid.Mcp` references the two
product MCP projects as libraries and registers their tool classes as they are (`ToolCatalog`, by
reflection, one instance each), except the three tools both define - `list_devices`,
`list_app_projects`, `get_app_output` - which `SharedTools` provides once (the profiler's richer
listings; app output from the debug session while one is active, from the device's logcat otherwise).
`DeviceArbiter` owns the device-global state: a call-tool filter refuses `profile_run`/`profile_start`
while a debug session holds the device and `launch_app`/`launch_from_config`/`attach_to_app` while a
profiling session holds it, naming the session to stop; the decision is a pure function with its own
tests. Registration `register-mcp.cmd` -> `%LOCALAPPDATA%\net-android`; the two product servers and
their scripts stay as they are. Tests: `tests/NetAndroid.Mcp.Tests` (no device: handshake, tool list
= union with the shared three once, the two device-free shared tools, no extra shipped assembly,
the arbiter); the debugger's and profiler's MCP end-to-end suites can be pointed at the unified
server through an environment variable for the device-level check.

Done (2026-09-06, late evening): builds (Debug and a Release publish to a scratch folder: 61 dlls,
the union of the two product servers, `Mono.Cecil` 0.11.6); `tests/NetAndroid.Mcp.Tests` 14/14 on
emulator-5554 (the device arbitration test included: launch_app through the unified server, then
profile_run refused naming stop_debugging, then not refused); the profiler's MCP tests 3/3 through
the unified server (`NAP_MCP_SERVER_DLL`) and through its own; the debugger's MCP end-to-end suite
through the unified server (`NAD_MCP_SERVER_DLL`): 19/22 on the first run, the three failures being
the two product-surface tests (now skipped under another server) and `list_app_projects` called with
the debugger's parameter name (`solutionOrFolder`, now accepted as an alias of `path`); +
the run against the debugger's own server are the last verification before the commit. The Cecil
question is answered by that suite: launch, breakpoints, locals and evaluation work with 0.11.6
loaded. Registration not performed on this machine (the user decides when to switch; the script
`register-mcp.cmd` says how to drop the two product registrations).

Component work resumes from `docs/debugger/TASK_RESUME.md` and `docs/profiler/TASK_RESUME.md`.
The report of the previous repository-level task (the merge and the shared device library,
finished 2026-09-06) is below.

## Report: building the monorepo and extracting NetAndroid.Device (2026-09-06)

### Commits per phase

| Phase | Commit | What |
|---|---|---|
| 1 import | `772681c` | Merge of the two rewritten histories: 62 debugger commits + 97 profiler commits + 1 merge commit (160). Each source history was rewritten under `debugger/` and `profiler/` with `git filter-branch` in scratch clones (new SHAs; messages, authors and dates kept), then joined with `--allow-unrelated-histories`. `git log --follow` on a moved file reaches each product's first commit. |
| 2 restructure | `5f2beb2` | Target layout, merged root files, path fixes; 321 tracked files, every one of the 328 source files accounted for (12 merged duplicates, 5 new root files). |
| 3 verify | no commit needed | Builds, suites, GUI, register scripts (below). |
| 4 documents | `1f33a95` | Root README, `docs/ARCHITECTURE.md`, `docs/KNOWN_UNKNOWNS.md`, the per-component documents. Rewritten once before anyone had pulled it, to drop three deletions the step 1 script had staged. |
| 5 publish | (push) | `gh repo create mca-software/net-android-debugger-profiler --private --source . --push`; owner `mca-software`, visibility PRIVATE, no description, no topics. |
| 6.1 | `28b205b` | `NetAndroid.Device` with `ProcessRunner` and `AdbLocator`; `AdbException` becomes a `ToolException`; `AdbLocatorTests` move. |
| 6.2 | `12d7945` | One `AdbClient`, one `DeviceInfo`, one `AdbResult`/`AdbException`; both Cores switched; adb-level logcat test and client tests in the library. |
| 6.3 | `260a3e8` | `DeviceControl` and `UiHierarchy` move; `UiHierarchyTests` whole, `DeviceControlTests` split (adb-only half in the library, the two session-bound tests stay). |
| 6.4 | `c98b0ed` | `DevicePropertyOverride` (apply with backup; restore for the profiler, clear for the debugger; shared foreign-value warning), `AppEnvironment` + `EnvironmentOverrideFile` moved and generalized, `DeviceGlobals`. |
| 6.5 | this commit | Constants documented, `docs/ARCHITECTURE.md` decision 1 closed with the rule, component `CLAUDE.md` rules, profiler U11 closed, `docs/TEST_CATALOG.md` for the library, the two product catalogs pointing at it. |

Final state: 168 commits, 334 tracked files, `examples/` intact (49 files), submodule
`ThirdParty/debugger-libs` at `837f524`.

### What was verified, and how

- Builds: `NetAndroidDebuggerProfiler.slnx`, `NetAndroidDebugger.slnx`, `NetAndroidProfiler.slnx`
  (the example app included) build with 0 errors after every phase and step; after phase 2 the
  warning sets were compared with clean builds of the two source repositories and were identical.
- Machine: .NET SDK 10.0.400, emulator `emulator-5554` = AVD `pixel_7_-_api_30` (API 30),
  Redmi Note 8 Pro attached but not used. `NAD_DEVICE_SERIAL` and `NAP_TEST_SERIAL` set to the emulator.
- Debugger suite (`tests/NetAndroidDebugger.Tests`, full, on the emulator): phase 3 169/170
  (the one failure, `TypeText_IntoTheFocusedField_ShowsUpInTheHierarchy`, is the api_30 Gboard
  crash loop the catalog documents; it passed alone); after step 2 163/163; after step 3
  146/146; after step 4 146/146 (an intermediate restore semantics failed two tests, fixed by
  `ClearAsync`, see the step 4 commit). The counts drop by exactly the tests that moved.
- Library suite (`tests/NetAndroid.Device.Tests`, on the emulator): 11/11 after step 2,
  28/28 after step 3, 36/36 after step 4.
- Profiler device-free suite: 111/111 at every step. GUI (`build-gui.cmd`) and its two test
  programs built. Register scripts: the debugger's run with `NAD_INSTALL_DIR` pointing at a
  scratch folder (both frontends and the notices published, the existing registration
  untouched); the profiler's publish step run alone (`dotnet publish` into a scratch folder),
  because the script registers when the server is absent.
- Profiler device suite (`Category=Device`, 32 tests, 7 skipped by design: six the reference application opt-ins
  and the build-map one), on the API 30 emulator: phase 3, first run 18 passed / 7 failed on a
  22-hour-old emulator; after a cold restart 23 passed / 2 failed
  (`Sampling_attach_to_running_debug_app_without_restart` hanging after its stop,
  `McpDeviceTests.Profile_start_and_profile_stop_round_trip` never reaching Collecting). After
  steps 2, 3 and 4 every attempt hung inside one EventPipe session and was killed at a
  15-25 minute cap; every session that completed passed. The cause of that cascade was found
  at the end of the day, from the profiler's own error text: a TestTarget process left alive by
  an interrupted session keeps `DOTNET_DiagnosticPorts=10.0.2.2:9000,suspend,connect` in its
  override environment and reconnects to the router of every later session, so the new session
  either waits for a runtime that never comes or times out on its stop; killing a run (which
  I did several times) is exactly what leaves such a process behind, and the untouched source
  repository hung the same way for the same reason. On the API 33 emulator (`api_33_0`, started later on request), with port 9000 free (a Docker
  service of the user's had held it during the day) and no stale app, the first runs failed on
  two transport stalls: a runtime connection whose reply the emulator holds until the app dies
  (the environment probe waited on it for minutes) and an event stream the router never ends
  after the stop although every byte is through. Both got a guard in the profiler's
  `EventPipeCollector` the same evening (probe deadline with retry on a new connection; drain
  that ends when the file stops growing), after which the suite passes on API 33: 25 passed,
  0 failed, 7 skipped by design, 7 min 14 s; API 30 the same: 25 passed, 0 failed, 7 skipped,
  6 min 31 s. Details in `docs/profiler/TASK_RESUME.md` and the
  profiler's notes.
- Not run: the the reference application tests (opt-in `NAP_REFAPP=1`), the build-time weave map test (needs a
  `-p:NapWeave=true` install), anything on the Redmi.

### Skipped or changed on purpose

- No `global.json` (neither repo had one; pinning 10.0.400 would break at the next SDK).
- `ThirdParty/debugger-libs` stays a git submodule (fork `csm101/debugger-libs`), as the
  debugger's documents already described its vendoring.
- Phase 2 code touches (listed in its commit): linked `Shared` sources renamed, the profiler
  TestTarget's targets import, test harness paths, the install-script tests, two tests that
  assumed one Android app per tree, the VS Code extension texts.
- Step 4: the debugger keeps clearing `debug.mono.extra` at shutdown (its tests' specification);
  only the profiler restores its property.

### For the day the repository goes public (apply by hand; nothing was written into the old repositories)

Description:
`Debugger and profiler for .NET for Android (MonoVM) apps, MAUI included: MCP servers for Claude Code and other agents, a Debug Adapter Protocol adapter for VS Code, EventPipe and IL-weaving profiling with a Delphi GUI.`

Topics: `dotnet, android, dotnet-android, maui, xamarin-android, debugger, profiler, mcp, mcp-server, model-context-protocol, debug-adapter-protocol, eventpipe, mono`

README for `csm101/net-android-debugger`:
> This repository has moved: the debugger now lives in [mca-software/net-android-debugger-profiler](https://github.com/mca-software/net-android-debugger-profiler), together with the profiler and the device layer they share.
> Sources are under `src/NetAndroidDebugger.*`, the documents under `docs/debugger/`; the full history came along.

README for `csm101/net-android-profiler`:
> This repository has moved: the profiler now lives in [mca-software/net-android-debugger-profiler](https://github.com/mca-software/net-android-debugger-profiler), together with the debugger and the device layer they share.
> Sources are under `src/NetAndroidProfiler.*`, the documents under `docs/profiler/`, the ProfileMeExample tutorial app under `examples/`; the full history came along.

Both old repositories are private today (checked with `gh repo view`), so nothing about them changes until then.

### Follow-ups, recommended order

1. The profiler's device suite: before any run, force-stop `com.mcasoftware.testtarget` on every
   attached emulator and kill leftover `dotnet-dsrouter` processes (a stale app reconnecting to
   port 9000 poisons every later session, on any emulator, since all reach the host as 10.0.2.2;
   the profiler's U10/U12b cover the port design). Then run the suite on one emulator at a time.
   The two transport stalls seen on API 33 have their guards in `EventPipeCollector` (evening of
   2026-09-06).
2. The unified MCP server: done (2026-09-06; the top of this file). Left out on purpose: one device
   selection and one app discovery for both Cores (a refactoring of the Cores, not of the frontend);
   retiring the two product MCP registrations (`register-mcp.cmd` says how; the user decides when).
2b. An agent skill shipped with the profiler (asked for on 2026-09-06): short, almost only rules and
   lists of traps with the tool names, distilled from the profiler's notes and KNOWN_UNKNOWNS into an
   operating guide. Contents: choosing the mode (sampling, instrumenting through the weaver, heap);
   traps: one dsrouter per host on port 9000, a profiled app left alive keeps reconnecting, a dirty
   override environment (and the `.pdb.naporig` leftover above), sampling's exclusive counts include
   tiny callees, instrumenting recording nothing (U23); reading results: ~1 ms samples, the `*_cpu`
   columns, `profile_annotate_source`, `heap_diff`, `profile_report` against the SQLite database;
   the combined debugger + profiler flow with the screen tools, and the same-device rule: attach to
   the debugged app after `remove_all_breakpoints` and clearing the exception rules, anything else
   needs `stop_debugging` first; UI-driving rules: resource ids from `get_ui_hierarchy`, the ASCII
   limit of `type_text`, the Gboard crash loop on API 30, `am start` not forking after a force-stop.
3. The CoreCLR engine question (`docs/KNOWN_UNKNOWNS.md` R2; debugger U8, profiler U9).
4. Smaller: unify the two TestTarget apps; give the debugger's device test classes a
   `Category=Device` trait like the profiler's, so one filter serves both suites; run the
   debugger suite from a folder whose `.vscode/launch.json` points at `TestTarget/Debugger`
   through VS Code once.

## Traps learned (kept for whoever works here next)

- `sed -i` in this Git Bash strips CR: edit CRLF files through PowerShell
  (`[IO.File]::ReadAllText` / `WriteAllText`), never with sed.
- `cmd /c name.cmd` does not search the current directory here: call scripts by path.
- The two device suites must not overlap on one device: the debugger sets `debug.mono.extra`
  device-wide while it runs.
- `register-mcp-profiler.cmd` registers when the server is absent: verify its publish step
  alone (`dotnet publish` into a scratch folder).
- Building `TestTarget/Profiler` under two application ids needs obj/bin cleaned in between:
  the SDK's incremental state keeps the previous manifest (XA0132 at install), and an APK
  installed by hand from that state lands on the incremental FS, where `adb pull` is refused.
- A profiler session killed mid-way leaves its override environment on the device (the backup
  lives only in the session's memory), and an incremental `-t:Install` does not rewrite it:
  `adb uninstall` then install puts the SDK default back. A leftover Docker service on 9000
  makes dsrouter fail with "Port 9000 is already in use" or lets the app connect to the wrong
  listener.
- A long-lived emulator hangs provider sessions; a cold restart
  (`AVD=pixel_7_-_api_30 SERIAL=emulator-5554 bash DevTools/scripts/ensure-emulator.sh`,
  adb and `ANDROID_SDK_ROOT` on the script's PATH) clears the state, and an orphan
  `dotnet-dsrouter` from a killed session keeps the next test run's output pipe open.
- The scripts that produced phase 6, with the drafts they copied, are kept in
  `C:\Athens\__ClaudeTools\monorepo-phase6\` (user-deletable).
