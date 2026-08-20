# Test Catalog

Living index of what the automated suite covers and what is still uncovered.

Status legend:
- `[x]` covered by an automated test in `tests/NetAndroidProfiler.Tests`
- `[ ]` known gap - add a TestTarget hook + assertion before fixing any bug
        in this area
- `[~]` partially covered

Conventions (mirroring the debugger project's discipline):
- Every brainstormed edge case becomes a **named test**, never prose.
- Green: plain `[Fact]`. Real known bug: `[Fact(Skip = "TODO-RED: <cause>")]`.
  Not yet feasible: stub body `Assert.Fail("not implemented")` +
  `Skip = "TODO: ..."`.
- Fast tests run against small recorded trace files checked into the repo;
  device tests are tagged `Category=Device`.
- Update this catalog in the same change set as the test or fix.

---

## A. Collection orchestration (Device/SessionTests, Category=Device, TestTarget Debug build on emulator-5556)
- [x] Sampling restart session -> parseable trace, Busy in hotspots - `Sampling_restart_session_finds_busy_method`
- [x] Instrumenting restart session (suspend, env injection, callspec) - `Instrumenting_restart_session_times_methods_and_counts_allocations`
- [x] Heap snapshot of running app - `Heap_snapshot_of_running_app_shows_retained_records`
- [x] Attach to running Debug app without restart (adb reverse) - `Sampling_attach_to_running_debug_app_without_restart`
- [x] Missing package fails with guidance - `Missing_package_fails_with_guidance`
- [ ] Missing diagnostics component fails with guidance (needs a TestTarget build without EnableDiagnostics)
- [ ] Release build: instrumenting refused unless MONO_DIAGNOSTICS baked
- [ ] Startup profiling via suspend captures app init (assert on OnCreate frames)
- [ ] Physical device (adb reverse 9000->9001) path - deferred (U10)
- [ ] Clean teardown: no orphan dsrouter processes, environment restored (assert override file equals backup)
- [ ] Stop() on a session without Duration ends collection

## B. Sampling analysis (Fast/SamplingAnalyzerTests, recorded testtarget-sampling-jit-20s)
- [x] Busy method appears in top exclusive CPU hotspots - `Busy_method_is_among_top_exclusive_cpu_hotspots`
- [x] Leaf method visible on JIT build - `Mix_leaf_method_is_visible_on_jit_build`
- [x] Wait frames classified, excluded from *_cpu - `Sleep_samples_are_classified_as_wait_and_excluded_from_cpu`
- [x] Inclusive root of worker thread - `Workload_loop_is_inclusive_root_of_the_worker_thread`
- [x] Call tree parent/child on known chain - `Call_tree_has_busy_under_loop_and_mix_under_busy`
- [x] Caller/callee edges - `Edges_link_loop_to_busy_and_busy_to_mix`
- [x] Every stacked sample resolved - `Every_sample_with_stack_is_resolved`
- [x] Name split (namespace/type/name/signature/module) - `Method_names_are_split_into_namespace_type_name`
- [x] Thread attribution - `Threads_are_reported_with_sample_counts`
- [ ] Symbolication of generic methods and async state machines
- [ ] AOT build: leaf attribution caveat reported (U15)

## C. Memory analysis
- [x] Exact allocation counts/sizes per type (provider path) - `Allocations_by_type_count_every_record_and_its_payload`
- [x] Alloc events attributed to innermost instrumented frame - `Allocations_are_attributed_to_the_innermost_instrumented_frame`
- [x] Unresolved pre-session vtables get placeholders - `Pre_session_vtables_get_placeholder_names_not_exceptions`
- [ ] TODO-RED U13: pre-session types resolve to names - `Pre_session_types_resolve_to_names` (skipped)
- [ ] Heap snapshot from live session: allocation-heavy type visible (device)
- [ ] Two snapshots diff (growth report)

## D. Instrumenting (Fast/MonoProfilerAnalyzerTests, recorded testtarget-monoprofiler-4s)
- [x] Enter/leave/alloc event counts exact - `Enter_leave_and_allocation_counts_match_the_recorded_trace`
- [x] Per-method timing + names via rundown - `NewRecord_timing_has_expected_call_count_and_names_resolved`
- [x] Timing tree nesting - `Timing_tree_nests_ctor_under_NewRecord_under_Allocate`
- [ ] Callspec session end-to-end on device: only filtered namespace instrumented
- [ ] Weaved APK (P3): deterministic call counts match known execution
- [ ] Async method timing attributed across await points
- [ ] Weaver skips excluded methods (getters/setters config)

## E. ResultStore / SQLite (Fast/ResultStoreTests)
- [x] Schema version stamped and checked on open - `Open_rejects_wrong_schema_version`
- [x] Sampling round-trip (hotspots, tree, edges) - `Sampling_round_trip_hotspots_tree_and_edges`
- [x] Instrumenting round-trip (timings, allocs, tree) - `Instrumenting_round_trip_timings_and_allocations`
- [x] Heap snapshot round-trip - `Heap_snapshot_round_trip`
- [ ] Large tree insert performance guard (U6)

## F. MCP end-to-end
- [ ] profile_run on TestTarget -> hotspots tool returns busy method
- [ ] profile_annotate_source maps samples onto source lines
- [ ] Error paths return MCP errors, never hang

## G. the reference application hardening (P3+)
- [ ] Sampling session on real app completes and analyzes
- [ ] Multi-assembly symbolication (App.Core, App.Shared, ...)
