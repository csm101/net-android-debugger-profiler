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

## A. Collection orchestration
- [ ] Build TestTarget with EnableDiagnostics and deploy
- [ ] Sampling session on emulator produces a parseable .nettrace
- [ ] Startup profiling via suspend captures app init
- [ ] gcdump collection succeeds
- [ ] Device (adb reverse) path - deferred (U10)
- [ ] Clean teardown: no orphan dsrouter/trace processes

## B. Sampling analysis
- [ ] Busy method appears in top hotspots (inclusive + exclusive)
- [ ] Call tree parent/child relations correct on a known call chain
- [ ] Thread attribution
- [ ] Recorded-trace regression: known trace -> exact expected hotspot table
- [ ] Symbolication of generic methods and async state machines

## C. Memory analysis
- [ ] Allocation-heavy type visible in gcdump report
- [ ] Alloc events attributed to allocating callsite (provider path)
- [ ] Two snapshots diff (growth report)

## D. Instrumenting (P3)
- [ ] Callspec session: enter/leave events for filtered namespace only
- [ ] Weaved APK: deterministic call counts match known execution
- [ ] Async method timing attributed across await points
- [ ] Weaver skips excluded methods (getters/setters config)

## E. ResultStore / SQLite
- [ ] Schema version stamped and checked on open
- [ ] Round-trip: analysis -> SQLite -> query equals in-memory model
- [ ] Large tree insert performance guard

## F. MCP end-to-end
- [ ] profile_run on TestTarget -> hotspots tool returns busy method
- [ ] profile_annotate_source maps samples onto source lines
- [ ] Error paths return MCP errors, never hang

## G. the reference application hardening (P3+)
- [ ] Sampling session on real app completes and analyzes
- [ ] Multi-assembly symbolication (App.Core, App.Shared, ...)
