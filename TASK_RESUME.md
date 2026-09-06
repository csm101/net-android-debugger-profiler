# Task resume (repository level)

Current task: build this monorepo from the two original repositories
(`csm101/net-android-debugger`, `csm101/net-android-profiler`) and extract the shared
device library. Phases: 0 preconditions, 1 import with history, 2 restructure,
3 verify, 4 documents, 5 publish (private, `mca-software`), 6 `src/NetAndroid.Device`.

Component in focus: both (repository-level restructuring).

## State

- Phase 0 done: both sources clean on `main`; baseline builds green (debugger 2 warnings,
  profiler 11 warnings, example 0 warnings, 0 errors everywhere).
- Phase 1 done: histories rewritten under `debugger/` and `profiler/` (git filter-branch,
  new SHAs), merged with one merge commit: 160 commits, 328 files, `--follow` verified.
- Phase 2 in progress: files moved into the target layout, root files merged, path fixes
  applied; next: submodule init, build the three solutions, verify the file mapping, commit.

## Next step if interrupted right now

Run `git status`; if the phase 2 commit does not exist yet, finish the verification
(`dotnet build NetAndroidDebuggerProfiler.slnx`, file mapping against the two source
lists in the scratchpad) and commit "Move both products into the monorepo layout".
