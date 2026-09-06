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
- The device layer's own tests (adb client, locator, screen, property override) live in
  `tests/NetAndroid.Device.Tests` since 2026-09-06 and are catalogued in `docs/TEST_CATALOG.md`.

---

## A. Collection orchestration (Device/SessionTests, Category=Device, TestTarget/Profiler Debug build on the device NAP_TEST_SERIAL names, emulator-5554 on this machine)
- [x] Sampling restart session -> parseable trace, Busy in hotspots - `Sampling_restart_session_finds_busy_method`
- [x] Instrumenting restart session (suspend, env injection, callspec honoured) -
  `Instrumenting_restart_session_times_methods` (trackAllocations off: see below)
- [x] One provider session carries timings and allocations together -
  `Instrumenting_provider_records_timings_and_allocations_together`. It skips while a device
  has stopped instrumenting (KNOWN_UNKNOWNS U23: restart the emulator), which is the state
  that briefly looked like the two being mutually exclusive
- [x] Heap snapshot of running app - `Heap_snapshot_of_running_app_shows_retained_records`
- [x] A heap session with default settings captures objects (it must not suspend the app) -
  `Heap_snapshot_with_default_settings_captures_objects`
- [x] Attach to running Debug app without restart (adb reverse) - `Sampling_attach_to_running_debug_app_without_restart`.
  Since 2026-09-06 also the specification of the drain after a stop: on the API 33 emulator the
  router never ends the event stream, and the session must still finish with every byte.
- [x] Missing package fails with guidance - `Missing_package_fails_with_guidance`
- [x] Missing diagnostics component fails with guidance, before anything is collected -
  `An_app_built_without_diagnostics_is_refused_with_guidance` (device; skips unless the
  companion build is installed: `-p:EnableDiagnostics=false
  -p:ApplicationId=com.mcasoftware.testtarget.nodiag -t:Install`)
- [x] Release build: instrumenting refused unless MONO_DIAGNOSTICS is baked, and the other
  modes never need it - `PrerequisiteTests` (fast: the rule, with its guidance text)
- [x] Startup profiling via suspend captures app init (MainActivity/OnCreate in the tree) -
  `Suspended_start_captures_the_app_initialisation`
- [ ] Physical device (adb reverse 9000->9001) path - deferred (U10)
- [x] Clean teardown: environment restored, no dsrouter left - `Session_restores_app_environment_and_leaves_no_dsrouter`
- [x] Stop() on a session without Duration ends collection and analyses what it has -
  `Stop_ends_a_session_that_was_started_without_a_duration`. Run right after the attach test it is
  also the specification of the environment probe's deadline: the runtime's first connection
  answers only when the app dies, and the session must ask again on the next one.

- [x] A session still runs when the app was left with an empty override environment file - `Session_runs_when_the_app_has_an_empty_override_environment_file`
- [x] Live control on a weaver session: snapshot grows, pause freezes, resume restarts, clear empties - `Weaver_session_can_snapshot_pause_and_clear_while_the_app_runs`
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
- [x] Symbolication of generic types, generic methods, and the MoveNext of async and
  iterator state machines, all resolving into the author's file -
  `GenericAndStateMachineSymbolsTests` (fast: tokens read from the sample assembly)
- [x] Leaf attribution characterized on device (long leaf attributed, tiny leaf folded into its caller) - `Sampling_attributes_a_long_running_leaf_method`
- [x] AOT: instrumenting refused, sampling warned but allowed (leaf frames land on the
  caller) - `Aot_blocks_instrumenting_and_only_warns_for_sampling`
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
- [x] Call tree mode: inclusive/exclusive split, one node per call path, repeated calls with
  their own min/max, a leave without its enter ignored, and nodes read back as timings and
  a tree - `CallTreeTests`
- [x] The tree engine on a device records the same shape for a fraction of the data -
  `Weaver_tree_engine_records_the_same_shape_for_a_fraction_of_the_data`
- [x] Weaver: exception paths balanced by finally-based Leave - same test (Boom / CatchAndReturn)
- [x] Weaver filter grammar incl. exclusions - `WeaveFilterTests.Namespace_type_method_and_exclusions`
- [x] Weaver records allocations by type and by allocating method - `Woven_methods_report_their_allocations_by_type_and_site` (fast) and asserted on device
- [x] Weaver on device: woven app assembly pushed to the override dir, NAP_PROFILER_OUT, session round trip, timings recorded, originals restored - `Weaver_instrumenting_session_times_woven_methods`
- [x] Iterator methods (yield return) instrumented like async bodies - `Iterator_state_machine_records_every_produced_item`, `Weaver_instruments_iterator_bodies_on_the_device`
- [x] Weaving targets file is valid XML and scoped to the app project - `Targets_file_is_well_formed_xml`, `Targets_only_weave_the_android_application_project`
- [x] Build-time weaving: map produced by the build, session consumes it without touching the device - `Build_time_weaving_session_uses_the_build_map` (skips unless TestTarget was installed from a `-p:NapWeave=true` build)
- [x] Weaver skips property accessors by default and can include them - `Property_accessors_are_skipped_by_default_and_can_be_included`
- [x] Async methods woven as stub + state machine - `Async_methods_are_woven_as_stub_and_state_machine`
- [x] Async state machine records every resumption (opt-in; CoreCLR only so far) - `Async_state_machine_records_every_resumption`
- [x] Async-body weaving runs on a real net9 app (the old TODO-RED was a launch defect, not the IL) - `Build_time_weaving_records_async_bodies_on_the reference application`
- [x] Build-time weaving exercised on the reference application (embedded assemblies, no device changes) - `Build_time_weaving_session_on_the reference application`

- [x] Iterator state machine: one resumption per item plus the terminating one - `Iterator_state_machine_records_every_produced_item`

## E. ResultStore / SQLite (Fast/ResultStoreTests)
- [x] Schema version stamped and checked on open - `Open_rejects_wrong_schema_version`
- [x] Sampling round-trip (hotspots, tree, edges) - `Sampling_round_trip_hotspots_tree_and_edges`
- [x] Instrumenting round-trip (timings, allocs, tree) - `Instrumenting_round_trip_timings_and_allocations`
- [x] Heap snapshot round-trip - `Heap_snapshot_round_trip`
- [x] Snapshots replace the results and leave a history - `Snapshots_replace_the_results_and_leave_a_history`
- [x] Clearing empties the results, keeps session and history - `Clearing_empties_the_results_and_keeps_the_session_row`
- [x] Large tree (200k nodes, 5k methods): write and GUI queries stay interactive - `Large_call_tree_stays_queryable`

## F. MCP end-to-end (Fast/McpServerTests: the server is spawned as a process over stdio)
- [x] initialize + tools/list expose the P1 tool surface - `Initialize_and_tools_list_expose_the_P1_tool_surface`
- [x] Read-only tools answer from a prepared sessions root (sessions, hotspots, report, tree, callers, threads) - `Read_only_tools_answer_from_the_prepared_session`
- [x] Error paths return MCP errors and the server stays alive - `Error_paths_return_mcp_errors_instead_of_hanging`
- [x] profile_run on TestTarget through MCP, and the read-only tools answering from the
  session it created without being told its id - `Profile_run_produces_a_session_the_read_only_tools_can_answer_from` (device)
- [x] profile_start / profile_status / profile_stop round trip -
  `Profile_start_and_profile_stop_round_trip` (device). Also the regression test for
  child processes inheriting the server's stdin: before the fix this hung for ever,
  because adb and dsrouter were eating the client's requests
- [x] profile_annotate_source through MCP, with an unknown source file reported as an
  error carrying guidance - `Profile_annotate_source_puts_the_figures_beside_the_method` (device)

## H3. GUI data layer (gui/tests/StoreTests.dpr, Delphi; run against real session databases)
- [x] Session identity, report rows, tree roots and expansion, details queries, segment history - `StoreTests`
- [x] A database written before the current schema still opens read-only (missing tables answer empty)
- [x] The heap chart gets a point per snapshot, with totals - `StoreTests`
- [ ] Automated in CI: today it is built and run by hand (see gui/tests/build-tests.cmd, then run StoreTests.exe with a session database)

## H4. GUI application (gui/tests/smoke.ps1; starts the real window, no device)
- [x] `--dialog=crash` produces a crash report whose **first** stack frame is the raising
  line, with unit and line number (checked by hand on 2026-08-28: `uMainForm.TMainForm.ShowPendingDialog
  (Line 1724, "uMainForm.pas")`). Deliberately outside the smoke loop, which treats any
  error log as a failure - it is the one case where the log must exist.
- [ ] A panel that is closed or on a background tab still receives what is written to it
  (the Log and the Summary buffer it): found by hand on 2026-08-26, when it was taking the
  application down through `--export`. The harness starts the window with the default
  layout, so it never had those panels hidden - worth a case that closes them first.
Every panel and dialog is opened against real session databases of each mode: the
crashes worth catching - a panel touching a control before it exists, a query that no
longer matches the schema - all happen while the window is being built.
- [x] Report, Call tree, Call graph, Source, Summary, Memory and Monitor open on a
  sampling, an instrumenting and a heap session without a startup error
- [x] The Settings and the Layouts dialog open on each of them
- [x] `--export=<file>` writes a non-empty csv and xlsx for each of them
- [ ] Automated in CI: today it is run by hand, like the data layer checks

## H7. Sources and machine (Fast/AppProjectFinderTests, Fast/AppBuilderTests, no device)
What a frontend fills a session in from, read from the project files rather than typed.
- [x] A solution offers its Android applications and not its libraries - `A_solution_offers_the_applications_and_not_the_libraries`
- [x] The package comes from the manifest when the project does not declare it - `The_package_can_come_from_the_manifest_when_the_project_does_not_declare_it`
- [x] The build output follows the configuration asked for - `The_build_output_follows_the_configuration_that_was_asked_for`
- [x] The assemblies to weave include the referenced projects, transitively - `The_assemblies_to_weave_include_the_referenced_projects`
- [x] EnableDiagnostics / EmbedAssembliesIntoApk are reported as declared, absent means unknown - `The_properties_a_profiling_build_needs_are_reported_as_declared`
- [x] A folder walk ignores the copies under bin and obj - `Walking_a_folder_ignores_the_copies_under_bin_and_obj`
- [x] A folder with nothing profilable is empty, not an error; a stray file is an error naming the reason - `A_path_that_holds_nothing_profilable_answers_an_empty_list`, `A_path_that_is_neither_solution_nor_project_says_so`
- [x] Callspec candidates come from the built assemblies; an unbuilt app offers none - `Callspec_candidates_come_from_the_assemblies_that_were_built`, `An_app_that_was_never_built_offers_no_candidates`
- [x] The build command carries exactly the properties a session needs, and nothing that was not asked for - `The_default_build_installs_a_diagnostics_enabled_fast_deployment_debug_build`, `Nothing_is_added_that_was_not_asked_for`
- [x] A project that does not exist is refused before msbuild starts - `A_project_that_does_not_exist_is_refused_before_msbuild_is_started`
- [x] Keeping the assemblies in the APK is stated explicitly, not left to the project's default - `Keeping_the_assemblies_in_the_apk_is_said_explicitly`
- [x] Weaving during the build passes the targets file and escapes the commas of the callspec; without a callspec it is refused; a build that does not weave says nothing about weaving - `Weaving_during_the_build_passes_the_targets_and_the_callspec`, `Weaving_during_the_build_without_a_callspec_is_refused`, `A_build_that_does_not_weave_says_nothing_about_weaving`
- [ ] The Setup dialog's own validation rules (which combinations it refuses): checked by hand on 2026-08-26 - a remembered runtime-provider choice on the net9 the reference application project showed the red line and disabled Start. No harness drives DevExpress controls, which is why this is not automated.
- [x] Clearing the app's deployed assemblies is an adb step, never an msbuild property - `Clearing_the_deployed_assemblies_is_an_adb_step_and_never_an_msbuild_property`
- [ ] A build actually run end to end, and the clearing of files/.__override__ with it (needs the android workload, a device and minutes): covered by hand through the GUI's Build & install

## Known flake
- `Fast/WeaverTests.Async_state_machine_records_every_resumption` failed once on
  2026-08-26 in a full fast-set run (`Assert.Equal(2, body.Calls)`) and passed on two
  immediate re-runs and inside the full suite. The tests of that collection share the
  collector's single output directory, which is the first place to look: an analysis that
  reads a file left by a neighbour counts calls that are not its own. Not yet reproduced
  on purpose, so not yet fixed - do not "fix" it by loosening the assertion.

## H10. What a build-time weave records (Fast/WeaveMapModeTests, no device)
A build-time weave bakes the recording mode into the app; the session cannot change it, so
the map declares it and the session follows. Found in the field the same day: a session
asked for the in-app call tree, the app had been built to write events, and it waited for
files nobody would write - with nothing said.
- [x] The map records tree or trace without disturbing the methods - `The_map_records_the_mode_the_app_was_built_with`
- [x] A map from before the mode existed reads as events, which is what those apps do - `A_map_from_before_the_mode_was_recorded_reads_as_events`
- [x] An unreadable header still yields the safe answer - `An_unreadable_header_still_yields_the_safe_answer`
- [ ] The session following a map that disagrees with the engine it was asked for (device): to check on the next the reference application run

## H9. The build-time weaver's backup (Fast/WeaveToolBackupTests, no device)
Found in the field on the reference application, 2026-08-27: the app crashed at startup with a
TypeLoadException for a class that had been deleted weeks earlier. nap-weave took its
`.naporig` backup as the input whenever one existed, so every build after the first threw
away the compilation that had just been produced and re-wove code from days before.
- [x] A second build is woven, not replaced by the first build's backup - `The_second_build_is_woven_and_not_replaced_by_the_first_ones_backup`
- [x] Weaving the same build twice does not instrument it twice (the invariant the backup exists for) - `Weaving_the_same_build_twice_does_not_instrument_it_twice`
- [x] An already woven assembly whose backup is gone is refused, not woven again - `An_already_woven_assembly_without_its_backup_is_refused_rather_than_woven_again`
- Verified to catch the defect: with the old logic two of the three fail.

## H8. Keeping results (Fast/ArchiveTests, no device)
An archive is a named copy of the result database, taken while the session goes on.
- [x] An archive is a result database that opens on its own, with its segment history - `An_archive_is_a_result_database_that_opens_on_its_own`
- [x] Archives are listed newest first, with no session running - `Archives_are_listed_newest_first_without_a_running_session`
- [x] The same name twice keeps both - `The_same_name_twice_keeps_both_archives`
- [x] A name that is not a file name still produces one, inside the session directory - `A_name_that_is_not_a_file_name_still_produces_one_inside_the_session`
- [x] No name given: still named and dated - `An_archive_without_a_name_is_still_named_and_dated`
- [x] A lost sidecar costs the pretty name and nothing else - `An_archive_whose_sidecar_was_lost_is_still_listed_and_openable`
- [x] A session with no results yet refuses instead of writing an empty archive - `A_session_with_no_results_yet_says_so_instead_of_writing_an_empty_archive`
- [x] An archive is read back through the registry by its path, which is how a frontend opens one - `An_archive_is_read_back_through_the_registry_by_its_path`
- [x] The service answers a session's archives from its directory, running or not - `The_archives_of_a_session_are_answered_from_its_directory_even_when_nothing_is_running`
- [x] Archiving from a live weaver session on a device: the archive is written, opens as an instrumenting session, is listed from disk and keeps its name - `ControlTests`

## H5. GUI control path (gui/tests/ControlTests.dpr, Delphi; needs a device)
Drives a real session through the client the GUI uses: nap.exe serve, device list,
prerequisite check, start, counters, the live controls, stop, and the database opened by
the GUI's own data layer.
- [x] A sampling session runs and its database opens as a sampling session - `ControlTests`
- [x] Snapshot is refused on the provider engine, with the reason - `ControlTests`
- [x] Snapshot, pause, resume and clear all answer on the weaver engine - `ControlTests`
- [x] A session cleared and stopped before new events arrive ends Ready with an empty
  result and a warning, not Failed - `ControlTests`
- [x] The Setup dialog's inputs, which need no device: the prerequisites are reported
  with dsrouter's install command, TestTarget is found from its folder with its package
  and both assemblies, and the callspec the sessions use is among the candidates -
  `RunSetupInputs`
- [ ] Automated in CI: it needs a device, like the .NET device suite

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
- [x] A session driven end to end over HTTP: devices, POST /sessions, counters while it
  collects, stop, and the database the service reports opened and read -
  `A_session_runs_from_start_to_database_over_http` (device)
- [x] /prereqs names every tool with its purpose, and dsrouter carries the command that installs it - `Prereqs_name_every_tool_and_how_to_get_the_ones_that_are_missing`
- [x] /projects lists the applications of a solution with package, assembly and output dir - `Projects_lists_the_android_applications_of_a_solution`
- [x] A path the finder cannot use, or no path at all, is a 400 - `A_path_the_finder_cannot_use_comes_back_as_400`
- [x] A build of a project that is not there is refused without starting a job - `A_build_of_a_project_that_is_not_there_is_refused_without_starting_a_job`
- [x] A tool the profiler does not install, or one with no install command, is refused rather than run - `A_tool_the_profiler_does_not_install_is_refused_rather_than_run`
- [x] A job this service did not start is named in the error - `A_job_this_service_did_not_start_is_named_in_the_error`
- [x] `nap serve` prints its port and ends itself when the process that owns it exits - `Serve_ends_itself_when_the_process_that_owns_it_exits`

## G. the reference application (opt-in: NAP_REFAPP=1, Category=the reference application)
- [x] Sampling restart session resolves V7 startup hot path - `Sampling_restart_session_on_the reference application_resolves_app_methods`
- [x] Weaver instrumenting records real timings - `Weaver_instrumenting_session_on_the reference application` (on-device weaving; opt-in with NAP_REFAPP_ONDEVICE=1 and a fast-deployment build, since the reference application ships with embedded assemblies)
- [x] Build-time weaving on the shipped configuration - `Build_time_weaving_session_on_the reference application`
- [x] Portable pdbs load and map tokens - `the reference application_pdbs_load_and_map_tokens`
- [ ] TODO-RED U20: runtime-provider instrumenting (net9 runtime crash) - `Instrumenting_session_on_the reference application_with_namespace_callspec`
- [x] Weaver with a wide callspec is NOT usable and reports why (measured: 7882 methods -> startup exceeds the 240 s marker wait; one type works) - covered by the guidance path in `Weaver_instrumenting_session_on_the reference application`
- [x] Async state machines woven by default report their resumptions - `Build_time_weaving_records_async_bodies_on_the reference application`
- [x] Multi-assembly in one session: both TestTarget and TestTarget.Support woven, named
  per module and resolved to their own source files -
  `One_session_symbolicates_methods_from_two_assemblies` (device). The app gained a
  second assembly to have the shape a real app has

## H6. Packaging (Fast/PackagingTests)
- [x] The tool version comes from the build, not a literal, and reaches the database
  stamp - `The_tool_version_comes_from_the_build_not_from_a_literal`
- [x] A tool shipped in the package wins over a globally installed one -
  `A_packaged_tool_wins_over_the_globally_installed_one`
- [x] bin\ finds the package's tools\ next to it - `The_tool_is_found_next_to_the_bin_directory_too`
- [x] An empty package directory falls through to the machine's own tools -
  `Without_a_packaged_copy_the_search_falls_through`
- [x] The zip is unpacked outside the repository and profiles from there: `nap doctor`
  reports the packaged dsrouter, `nap run` writes a session, `install.cmd /name <n>`
  registers and `/remove` unregisters it, and the packaged GUI opens a session from
  the package (manual, 2026-08-23; the run needs a device)
- [ ] The same on a machine without the .NET SDK and without this repository
ap.exe run against the emulator)

## H. Licensing (Fast/ThirdPartyNoticesTests)
- [x] Every package the shipped build depends on is acknowledged in THIRD-PARTY-NOTICES.txt (read from the .deps.json of NetAndroidProfiler.Mcp and nap-weave, so a new dependency fails the test) - `Every_distributed_package_is_acknowledged`
- [x] The notices carry the full MIT and Apache-2.0 texts and no copyleft license - `Notices_carry_the_full_license_texts`

