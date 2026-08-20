# Test Catalog

Living index of what the automated suite covers and what is still uncovered.

Status legend:
- `[x]` covered by an automated test in `tests/NetAndroidDebugger.Tests`
- `[ ]` known gap — add a TestTarget hook + assertion before fixing any bug in
        this area
- `[~]` partially covered

Conventions (mirroring the Delphi project's discipline):
- Every brainstormed edge case becomes a **named test**, never prose.
- Runnable-and-green: plain `[Fact]`.
- Real known bug: `[Fact(Skip = "TODO-RED: <root cause>")]`.
- Not yet feasible: stub whose body is `Assert.Fail("not implemented")` plus
  `Skip = "TODO: ..."` — removing Skip forces a real implementation.
- Update this catalog in the same change set as the test or fix.

---

## A. Launch / attach lifecycle
- [ ] Deploy + launch TestTarget on emulator, debugger attaches
- [ ] Attach to already-running debuggable app
- [ ] Detach leaves app running
- [ ] App exit is reported as session end
- [ ] Debugger disconnect mid-run (recovery behavior)

## B. Breakpoints
- [ ] Source-line breakpoint hit
- [ ] Breakpoint in not-yet-loaded assembly resolves when loaded
- [ ] Conditional breakpoint
- [ ] Hit-count breakpoint
- [ ] Remove / remove-all while running

## C. Stepping
- [ ] Step over / into / out at a plain call site
- [ ] Step through async/await
- [ ] Step over a call that raises an exception

## D. Stack and threads
- [ ] Call stack at breakpoint (managed frames, correct lines)
- [ ] Multiple threads listed; stacks per thread
- [ ] Main/UI thread identified

## E. Locals and values
- [ ] Primitives (int, long, bool, char, string, double, decimal, DateTime)
- [ ] Enums and flags
- [ ] Object expansion (fields, properties)
- [ ] Arrays / List<T> / Dictionary<K,V> expansion
- [ ] Null and uninitialized locals
- [ ] Generic types display

## F. Evaluate
- [ ] Simple expression (local arithmetic)
- [ ] Member access / method call (side-effect policy documented)
- [ ] Invalid expression yields error, not crash

## G. Exceptions
- [ ] First-chance filter (break on thrown)
- [ ] Unhandled exception reported with details
- [ ] Exception type filtering

## H. Android specifics
- [ ] Logcat/app output capture during session
- [ ] Activity restart (rotation) mid-session behavior
- [ ] Attach over `adb connect` (WiFi device) — deferred (U9)

## I. MCP end-to-end
- [ ] Tool round-trip: launch → breakpoint → wait_until_stopped → locals → continue
- [ ] get_compact_debug_snapshot content
- [ ] Error paths return MCP errors, never hang
