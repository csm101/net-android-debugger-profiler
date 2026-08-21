# Profiling a .NET Android app with net-android-profiler

Practical flows for the MCP tools. The app must be built with
`-p:EnableDiagnostics=true`; see [APP_SETUP.md](APP_SETUP.md) for what each
mode additionally needs.

## 1. Find where the time goes (CPU sampling)

```
list_devices
profile_run deviceSerial=emulator-5556 packageName=com.acme.app mode=sampling durationSeconds=20
profile_hotspots top=20              # exclusive CPU samples per method
profile_tree                         # thread roots, then profile_tree nodeId=<id> to drill down
profile_callers method=Acme.Sync.Run # who calls the hot method
```

Reading the numbers:

- Counts are **samples** (~1 ms each), not milliseconds of wall clock.
- `inclusive` = the method was somewhere on the stack, `exclusive` = it was on
  top. The `*_cpu` columns drop samples taken while the thread was blocked in
  Sleep/Wait, which is what you want when hunting CPU cost; use the plain
  columns to find blocking instead.
- A method's exclusive samples **include the time of its very short callees**:
  the MonoVM sampler does not report tiny leaf frames. Read a hotspot as "this
  method plus its trivial helpers".
- On profiled-AOT builds, leaf frames of AOT code are lost more aggressively;
  profile a JIT build (`RunAOTCompilation=false`) when attribution matters.

## 2. Measure exactly (instrumenting)

Sampling tells you *where* to look; instrumenting tells you *how many times*
and *how long*, deterministically. It is expensive, so keep the filter narrow -
the type or few types the sampling profile pointed at.

```
profile_run deviceSerial=... packageName=... mode=instrumenting \
            callspec="T:Acme.Sync.SyncService" durationSeconds=20
profile_timings top=20               # calls, total/self ms, min/max, exception exits
profile_tree                         # the same data as a call tree
alloc_report top=20                  # every allocation by type (provider engine)
alloc_report bySite=true             # ... attributed to the allocating method
```

Two engines produce the same tables:

| | `engine=provider` (default) | `engine=weaver` |
|---|---|---|
| net10 apps | yes | yes |
| net9 apps | **no** - crashes the runtime (KNOWN_UNKNOWNS U20) | yes |
| allocations | yes, with sizes | counts by type and allocating method (no sizes) |
| app requirements | `MONO_DIAGNOSTICS` in the environment (Debug builds: injected automatically) | the app must run the woven assemblies (fast deployment, or build-time weaving) |

Weaver on a fast-deployment build:

```
profile_run ... mode=instrumenting engine=weaver callspec="T:Acme.Sync.SyncService" \
            weaveAssemblies="Acme.Droid" weaveReferenceDirs="C:\src\acme\Acme.Droid\bin\Debug\net9.0-android35.0"
```

Weaver on an app that embeds its assemblies (build it once with weaving, then
profile without touching the device):

```
dotnet build -c Debug -t:Install -p:EnableDiagnostics=true \
             -p:NapWeave=true -p:NapCallspec="T:Acme.Sync.SyncService"
profile_run ... mode=instrumenting engine=weaver weaveMapPath="...\bin\Debug\net9.0-android35.0\nap-weave.map"
```

Notes: property accessors are skipped by default (`weavePropertyAccessors=true`
to include them). An async method produces two entries: the stub, whose time is
only the synchronous part up to the first await, and `<method> (async body)`,
the state machine, whose calls are resumptions and whose time excludes the
awaits. Both are woven by default; `weaveAsyncBodies=false` (nap-weave:
`--no-async-bodies`) keeps the stubs only. Iterators (`yield return`) are
stub-only for now (KNOWN_UNKNOWNS U8).

## 3. Memory

```
profile_run ... mode=heap                      # one snapshot of the live heap
heap_report top=40                             # live objects and bytes per type

profile_run ... mode=heap snapshots=2 snapshotIntervalSeconds=60
heap_diff                                      # what grew between the two snapshots
```

`heap_diff` orders by bytes gained: the types at the top are the leak
candidates. Exercise the suspect feature between the two snapshots (the app
keeps running, `launch=attach` avoids restarting it).

For allocation *rate* rather than live set, use an instrumenting session with
`trackAllocations=true` and `alloc_report bySite=true`: it shows which method
allocates what, exactly, with no sampling.

## 4. Source and reporting

```
profile_annotate_source symbolsDir="...\bin\Debug\net9.0-android35.0" sourceFile="Sync/SyncService.cs"
profile_report                                 # summary of the session
profile_sessions                               # everything profiled so far
get_app_output deviceSerial=... packageName=...   # logcat of the app
```

Figures are per method (MonoVM gives no per-line samples), printed next to the
method's first line with its range marked.

## 5. Long sessions

`profile_start` ... exercise the app ... `profile_stop`. `profile_status`
shows the state and the last log lines while it runs.

## Session storage

One directory per session under `%LOCALAPPDATA%\net-android-profiler\sessions`
(override with `NAP_SESSIONS_ROOT`): `session.json` (what was asked),
`session.db` (results, schema in ARCHITECTURE.md), `trace.nettrace` (opens in
PerfView or Visual Studio), `session.log`. Every read-only tool accepts
`sessionId`, defaulting to the most recent session, so results stay queryable
long after the run.
