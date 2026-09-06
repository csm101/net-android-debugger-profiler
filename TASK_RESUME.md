# Task resume (repository level)

Current task: build this monorepo from the two original repositories
(`csm101/net-android-debugger`, `csm101/net-android-profiler`) and extract the shared
device library. Phases: 0 preconditions, 1 import with history, 2 restructure, 3 verify,
4 documents, 5 publish (private, `mca-software/net-android-debugger-profiler`),
6 `src/NetAndroid.Device` in five strangler steps.

Component in focus: both (repository-level restructuring); phase 6 touches both Cores.

## State

- Phase 0 done: both sources clean on `main`; baseline builds green (debugger 2 warnings,
  profiler 11, example 0; 0 errors everywhere).
- Phase 1 done: histories rewritten under `debugger/` and `profiler/` with `git filter-branch`
  (new SHAs, same messages/authors/dates), joined by one merge commit (`772681c`): 160 commits,
  328 files, `git log --follow` reaches each source's first commit.
- Phase 2 done, commit `5f2beb2`: target layout, merged root files, path fixes; 321 tracked files.
- Phase 3 done (no fix commit needed): the three solutions build with the sources' exact
  warning sets; example app builds; profiler fast suite 111/111; debugger full suite on
  `emulator-5554` 169/170, the one failure (`TypeText_IntoTheFocusedField...`) is the api_30
  image's Gboard crash loop documented in the debugger's TEST_CATALOG and passes alone;
  profiler device suite on the same emulator after a cold restart 23 passed, 7 skipped
  (six the reference application opt-ins, the build-map one), 2 failed
  (`Sampling_attach_to_running_debug_app_without_restart`, `McpDeviceTests.Profile_start_and_profile_stop_round_trip`):
  both hang after the session's stop command until the timeout, on binaries built before
  any phase 6 change; the same two tests are being run from the untouched source repository
  to confirm they pre-exist on this image. GUI and its test programs build; both register
  scripts' publish steps verified into the scratchpad (registration untouched).
- Phase 4 documents written: root README, docs/ARCHITECTURE.md, docs/KNOWN_UNKNOWNS.md,
  per-component notes ("now lives here", corrected commands, CoreCLR and shared-layer entries).
- Phase 5 done: `mca-software/net-android-debugger-profiler`, private, `main` pushed (the documents
  commit was rewritten once before anyone pulled, to drop three deletions the step 1 script had staged).
- Phase 6 step 1 committed and pushed (`28b205b`): library, ProcessRunner, AdbLocator, tests.
- Phase 6 step 2 in the working tree (unified `AdbClient`, `DeviceInfo`, both Cores switched, old
  clients deleted, adb-level tests moved): builds; library 11/11, debugger 163/163 on the emulator,
  profiler device suite being re-run on a cold-restarted emulator after a hung heap-snapshot session
  (the same hang the untouched source showed). Commit when it matches the known baseline
  (23 passed, 7 skipped, the two stop-path hangs).
- Steps 3, 4, 5: scripts and sources drafted in `C:\Athens\__ClaudeTools\monorepo-phase6\`
  (`phase6-step3.ps1`, `phase6-step4.ps1`, `phase6-step5.ps1`, folders `step2..step5`,
  `device-suites.ps1` runs the three suites, `github-texts.md` holds the texts for the public day).

## Next step if interrupted right now

Read `X:\Temp\...\scratchpad\profiler-step2-rerun.log` or re-run
`dotnet test tests/NetAndroidProfiler.Tests/NetAndroidProfiler.Tests.csproj --no-build --filter "Category=Device"`
with `NAP_TEST_SERIAL=emulator-5554`; commit step 2 ("Give both products one adb client"), push;
then `pwsh -File C:\Athens\__ClaudeTools\monorepo-phase6\phase6-step3.ps1`, build, unit tests, the three
suites (`device-suites.ps1 -Tag step3`), commit, push; the same for step 4 and step 5; then the
final report here.
## Traps learned so far

- `sed -i` in this Git Bash strips CR: edit CRLF files through PowerShell
  (`[IO.File]::ReadAllText` / `WriteAllText`), never with sed.
- `cmd /c name.cmd` does not search the current directory here: call scripts by path.
- The two device suites must not overlap on one device: the debugger sets `debug.mono.extra`
  device-wide while it runs.
- `register-mcp-profiler.cmd` registers when the server is absent: its publish step was
  verified alone (`dotnet publish` into the scratchpad), never the script as a whole.
- Building `TestTarget/Profiler` under two application ids needs obj/bin cleaned in between:
  the SDK's incremental state keeps the previous manifest (XA0132 at install), and an APK
  installed by hand from that state lands on the incremental FS, where `adb pull` is refused.
- A 22-hour-old emulator hung four provider sessions; a cold restart
  (`AVD=pixel_7_-_api_30 SERIAL=emulator-5554 bash DevTools/scripts/ensure-emulator.sh`,
  adb and ANDROID_SDK_ROOT on the script's PATH) cleared those.
