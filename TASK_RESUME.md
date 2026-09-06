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
- Phase 6 step 1 wired in the working tree (library, ProcessRunner, AdbLocator, tests;
  both Cores referencing it): debugger side builds and passes its device-free classes;
  profiler side to build and test, then commit.

## Next step if interrupted right now

Commit phase 4 (`README.md`, `TASK_RESUME.md`, `docs/`), then phase 5
(`gh repo create mca-software/net-android-debugger-profiler --private --source . --push`,
verify owner and visibility), then build `NetAndroidProfiler.slnx`, run its fast tests and
commit step 1 ("Share the process runner and the adb locator through NetAndroid.Device"),
push; then steps 2-5 from the scratchpad drafts (`step2/`, `step4/`, `step5/`).

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
