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
- Every device test class belongs to the xUnit collection `"device"`
  (`DisableParallelization`): the emulator, adb and the dsrouter port are
  single-instance resources, and two classes running at once profile each
  other's app.
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
- [x] Clean teardown: environment restored, no dsrouter left - `Session_restores_app_environment_and_leaves_no_dsrouter`
- [ ] Stop() on a session without Duration ends collection

- [x] A session still runs when the app was left with an empty override environment file - `Session_runs_when_the_app_has_an_empty_override_environment_file`
- [x] U5: collection stops at the trace size limit, keeps a valid trace and warns - `Collection_stops_when_the_trace_reaches_its_size_limit`
- [x] Iterator bodies are instrumented on the device (one call per item produced) - `Weaver_instruments_iterator_bodies_on_the_device`
- [x] Attach tests configure the app's diagnostics port themselves (they used to rely on leftovers of earlier sessions) - `Sampling_attach_to_running_debug_app_without_restart`, `Heap_snapshot_of_running_app_shows_retained_records`, `Two_heap_snapshots_support_a_growth_diff`
- [x] A port already in use is detected before spawning dsrouter - `Fast/DsRouterTests`

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
- [x] Leaf attribution characterized on device (long leaf attributed, tiny leaf folded into its caller) - `Sampling_attributes_a_long_running_leaf_method`
- [ ] AOT build: leaf attribution caveat surfaced as a session warning
- [x] Method tokens -> portable pdb source ranges (Fast/PortablePdbSymbolsTests): `Loads_testtarget_pdb_and_lists_its_documents`, `Methods_in_document_have_line_ranges`, `Sampled_method_tokens_resolve_to_source_ranges`, `Unknown_module_or_token_returns_null`

## C. Memory analysis
- [x] Exact allocation counts/sizes per type (provider path) - `Allocations_by_type_count_every_record_and_its_payload`
- [x] Alloc events attributed to innermost instrumented frame - `Allocations_are_attributed_to_the_innermost_instrumented_frame`
- [x] U13 (runtime limitation, not a bug): types that predate the session keep exact counts and carry a label saying why they have no name - `Types_that_predate_the_session_are_labelled_not_dropped`
- [x] Heap snapshot from live session: allocation-heavy type visible (device) - `Heap_snapshot_of_running_app_shows_retained_records`
- [x] Two snapshots in one session + growth diff - `Two_heap_snapshots_support_a_growth_diff` (device) and `Heap_diff_reports_growth_and_disappearance` (fast: growth, stability, disappearance, new types)

## D. Instrumenting (Fast/MonoProfilerAnalyzerTests, recorded testtarget-monoprofiler-4s)
- [x] Enter/leave/alloc event counts exact - `Enter_leave_and_allocation_counts_match_the_recorded_trace`
- [x] Per-method timing + names via rundown - `NewRecord_timing_has_expected_call_count_and_names_resolved`
- [x] Timing tree nesting - `Timing_tree_nests_ctor_under_NewRecord_under_Allocate`
- [ ] Callspec session end-to-end on device: only filtered namespace instrumented (blocked on net9 targets - U20)
- [x] Weaver: deterministic call counts match known execution (Fib recursion, nesting, values) - `Weave_execute_and_analyze_end_to_end`
- [x] Weaver: exception paths balanced by finally-based Leave - same test (Boom / CatchAndReturn)
- [x] Weaver filter grammar incl. exclusions - `WeaveFilterTests.Namespace_type_method_and_exclusions`
- [x] Weaver records allocations by type and by allocating method - `Woven_methods_report_their_allocations_by_type_and_site` (fast) and asserted on device
- [x] Weaver on device: woven app assembly pushed to the override dir, NAP_PROFILER_OUT, session round trip, timings recorded, originals restored - `Weaver_instrumenting_session_times_woven_methods`
- [ ] Iterator methods (yield return) instrumented like async bodies (U8 leftover)
- [x] Weaving targets file is valid XML and scoped to the app project - `Targets_file_is_well_formed_xml`, `Targets_only_weave_the_android_application_project`
- [x] Build-time weaving: map produced by the build, session consumes it without touching the device - `Build_time_weaving_session_uses_the_build_map` (skips unless TestTarget was installed from a `-p:NapWeave=true` build)
- [x] Weaver skips property accessors by default and can include them - `Property_accessors_are_skipped_by_default_and_can_be_included`
- [x] Async methods woven as stub + state machine - `Async_methods_are_woven_as_stub_and_state_machine`
- [x] Async state machine records every resumption (opt-in; CoreCLR only so far) - `Async_state_machine_records_every_resumption`
- [ ] TODO-RED U8: async-body weaving prevents a real net9 app from starting (device)
- [x] Build-time weaving exercised on the reference application (embedded assemblies, no device changes) - `Build_time_weaving_session_on_the reference application`

- [x] Iterator state machine: one resumption per item plus the terminating one - `Iterator_state_machine_records_every_produced_item`

## E. ResultStore / SQLite (Fast/ResultStoreTests)
- [x] Schema version stamped and checked on open - `Open_rejects_wrong_schema_version`
- [x] Sampling round-trip (hotspots, tree, edges) - `Sampling_round_trip_hotspots_tree_and_edges`
- [x] Instrumenting round-trip (timings, allocs, tree) - `Instrumenting_round_trip_timings_and_allocations`
- [x] Heap snapshot round-trip - `Heap_snapshot_round_trip`
- [x] Large tree (200k nodes, 5k methods): write and GUI queries stay interactive - `Large_call_tree_stays_queryable`

## F. MCP end-to-end (Fast/McpServerTests: the server is spawned as a process over stdio)
- [x] initialize + tools/list expose the P1 tool surface - `Initialize_and_tools_list_expose_the_P1_tool_surface`
- [x] Read-only tools answer from a prepared sessions root (sessions, hotspots, report, tree, callers, threads) - `Read_only_tools_answer_from_the_prepared_session`
- [x] Error paths return MCP errors and the server stays alive - `Error_paths_return_mcp_errors_instead_of_hanging`
- [ ] profile_run on TestTarget through MCP (device test)
- [ ] profile_start / profile_stop round trip
- [ ] profile_annotate_source through MCP on a prepared session

## H2. Control service (Fast/ControlServiceTests, no device)
- [x] /health reports version, sessions root and port - `Health_reports_the_version_and_where_sessions_live`
- [x] An empty sessions root lists nothing - `An_empty_sessions_root_lists_no_sessions`
- [x] Unknown route answers 404 naming the path - `An_unknown_route_is_a_404_naming_what_was_asked`
- [x] A spec Core rejects becomes 400 with the engine's own guidance - `A_spec_the_engine_rejects_comes_back_as_400_with_the_guidance`, `Instrumenting_without_a_callspec_is_refused_before_touching_a_device`
- [x] Malformed JSON is 400, not 500 - `Malformed_json_is_a_400_not_a_500`
- [x] A session this service did not start cannot be controlled - `A_session_this_service_did_not_start_cannot_be_controlled`
- [x] pause/resume/snapshot/clear answer 501 saying what is missing - `The_live_control_verbs_answer_501_until_the_engine_supports_them`
- [x] /shutdown signals the host - `Shutdown_signals_the_host_to_stop`
- [x] A POST without Content-Length is rejected by the Windows HTTP stack (documented, not a service bug) - `A_post_without_a_content_length_is_rejected_by_the_http_stack`
- [ ] A session driven end to end over HTTP (device test; done by hand 2026-08-21: 1338 samples)

## G. the reference application (opt-in: NAP_REFAPP=1, Category=the reference application)
- [x] Sampling restart session resolves V7 startup hot path - `Sampling_restart_session_on_the reference application_resolves_app_methods`
- [x] Weaver instrumenting records real timings - `Weaver_instrumenting_session_on_the reference application` (on-device weaving; opt-in with NAP_REFAPP_ONDEVICE=1 and a fast-deployment build, since the reference application ships with embedded assemblies)
- [x] Build-time weaving on the shipped configuration - `Build_time_weaving_session_on_the reference application`
- [x] Portable pdbs load and map tokens - `the reference application_pdbs_load_and_map_tokens`
- [ ] TODO-RED U20: runtime-provider instrumenting (net9 runtime crash) - `Instrumenting_session_on_the reference application_with_namespace_callspec`
- [x] Weaver with a wide callspec is NOT usable and reports why (measured: 7882 methods -> startup exceeds the 240 s marker wait; one type works) - covered by the guidance path in `Weaver_instrumenting_session_on_the reference application`
- [x] Async state machines woven by default report their resumptions - `Build_time_weaving_records_async_bodies_on_the reference application`
- [ ] Multi-assembly symbolication in one session (App.Core, App.Shared, ...)

## H. Licensing (Fast/ThirdPartyNoticesTests)
- [x] Every package the shipped build depends on is acknowledged in THIRD-PARTY-NOTICES.txt (read from the .deps.json of NetAndroidProfiler.Mcp and nap-weave, so a new dependency fails the test) - `Every_distributed_package_is_acknowledged`
- [x] The notices carry the full MIT and Apache-2.0 texts and no copyleft license - `Notices_carry_the_full_license_texts`

