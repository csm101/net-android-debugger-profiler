# Task resume (repository level)

No repository-level task is in progress. The last one, the merge of the two repositories
into this monorepo and the extraction of the shared device library, finished on 2026-09-06;
its report is below. Component work resumes from `docs/debugger/TASK_RESUME.md` and
`docs/profiler/TASK_RESUME.md`. The next repository-level tasks, in the recommended order,
are the follow-ups at the end of this file.

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

Final state: 166 commits, 334 tracked files, `examples/` intact (49 files), submodule
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
  repository hung the same way for the same reason. On the API 33 emulator (`api_33_0`, started
  later on request) the app connects to the router but the session stays in `WaitingForApp`
  past the test's 4-minute cancel, and the test host crashed once; that path is the profiler's
  collection code, untouched by the move, and was not resolved today.- Not run: the the reference application tests (opt-in `NAP_REFAPP=1`), the build-time weave map test (needs a
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
   the profiler's U10/U12b cover the port design). Then run the suite on one emulator at a time
   and, on API 33, find why `WaitForRuntimeAsync` never sees the connected runtime.2. The unified MCP server (`docs/ARCHITECTURE.md`, decision 2): one server over both Cores
   owning the device-global state that `DeviceGlobals` names.
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
- A long-lived emulator hangs provider sessions; a cold restart
  (`AVD=pixel_7_-_api_30 SERIAL=emulator-5554 bash DevTools/scripts/ensure-emulator.sh`,
  adb and `ANDROID_SDK_ROOT` on the script's PATH) clears the state, and an orphan
  `dotnet-dsrouter` from a killed session keeps the next test run's output pipe open.
- The scripts that produced phase 6, with the drafts they copied, are kept in
  `C:\Athens\__ClaudeTools\monorepo-phase6\` (user-deletable).
