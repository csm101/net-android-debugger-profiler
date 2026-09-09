# Reading the results

Every session ends in a database; the read-only tools are views on it. All of them take
`sessionId` (default: the last session, or an archive's path).

## Sampling

`profile_hotspots` (default view), `profile_flat` (every method, by inclusive CPU),
`profile_tree` (thread roots, then children by `nodeId`), `profile_callers` / `profile_callees`
(edges with their sample counts), `profile_threads`, `profile_annotate_source`.

Columns: `incl` (the method was somewhere on the stack), `excl` (it was on top), `incl_cpu` /
`excl_cpu` (the same, without samples of threads blocked in a wait), the percentage of the
samples with a stack.

What the numbers mean on MonoVM:

- One sample is about one millisecond of a thread's life, whether it was running or waiting.
  A thread sleeping in a loop piles samples under the wait primitive; that is why `*_cpu`
  exists. "Where does the wall-clock time go" reads the plain columns; "what burns CPU"
  reads the `_cpu` ones.
- The effective rate is a few hundred samples per second per thread, so a 20 s session on a
  busy thread has thousands of samples; a method with 30 of them is noise.
- About half of all samples have no managed stack (native threads, the runtime itself); the
  percentage column is over the samples that do.
- **Exclusive time absorbs tiny callees.** The sampler reports the innermost method that owns a
  real frame; a very short leaf never gets one. `Parse` at 1,300 exclusive samples with no
  `Char.IsDigit` anywhere means "Parse and whatever small things it calls". Long-bodied methods
  are attributed correctly.
- **No line-level samples.** A frame has one fixed address per method, so
  `profile_annotate_source` puts the method's figures on its first line and marks the range.
  Reading it: which methods of this file matter, not which line.
- AOT-compiled methods hide their leaves in the caller; JIT builds attribute them.

A worked reading: the UI thread shows `Button.OnClick` 2,400 inclusive / 12 exclusive, its
callee `ReportBuilder.Build` 2,380 / 2,300 exclusive. The time is in `Build` and its tiny
callees; the next step is instrumenting with `callspec: T:...ReportBuilder` to see which of its
methods, and how often.

## Instrumenting

`profile_timings` (per method: calls, total, self, min, max, average, exception exits),
`profile_tree` (calls and total/self milliseconds per path), `alloc_report`.

- Figures are exact, per completed call. A call that never returns in the window is not timed.
- `<Type>.<Method> (async body)`: the resumptions of an async method, its time excluding the
  awaits. The plain row of the same method is the synchronous stub: a few milliseconds for a
  method that visibly takes seconds means the work is in the body row, or in the awaited calls.
- `<Type>.<Method> (iterator body)`: one call per item a `yield` sequence produced. A sequence
  enumerated twice shows twice the calls.
- `engine: weaver-tree` keeps a calling-context tree in the app: callers, callees, critical
  path and min/max survive; the order of calls and each call's own duration do not.
  `engine: weaver` records every call: use it when the order or a single slow call matters,
  at a higher cost and trace size.
- `alloc_report` lists allocations by type (count, bytes); `bySite: true` attributes them to
  the instrumented method that allocated, which only the `weaver` engine records.
- Overhead is per call, in the order of microseconds and tens of bytes per event. A method
  called a million times per second cannot be instrumented; that is what sampling is for.

A worked reading: `PriceService.GetPrice` 48,000 calls, 3.1 s total, 0.06 ms average;
its caller `CartPage.Refresh` 12 calls. Four thousand price lookups per refresh is the bug,
not the lookup's speed.

## Heap

`heap_report` (live objects per type: count, bytes, one snapshot), `heap_diff` (two snapshots
of one session: growth per type, ordered by bytes gained).

- A leak hunt is `profile_run` with `mode: heap`, `snapshots: 2` and an interval long enough
  for the user to do the thing that leaks a few times. The types at the top of `heap_diff`
  are candidates; framework collections growing alongside an application type point at the
  application type.
- Snapshots never suspend the app; taking one costs a pause of seconds on a large heap.
- Type names of objects allocated before the session may be missing: take the first snapshot
  early and the second late, rather than both late.

## Threads

`profile_threads` lists the threads with their sample counts. A frozen UI is either the UI
thread busy (its samples are in application code) or the UI thread waiting for another one
(its samples are in a wait; the other thread's samples say what it was doing).

## Comparing two sessions

Run the same spec twice (before and after a change, or with and without the suspected
feature), then call the same read-only tool with each `sessionId`. Compare percentages and
call counts rather than raw sample counts: durations never match exactly.

## A picture of it

Every view above is also a panel of the profiler's GUI, which draws it on request:
`gui_open` (a session), `gui_view` (a panel, a method to focus, and for the call graph
`expand` - how many levels of callees to open), `gui_capture` (the PNG).
The window is not shown while this happens, so it works when the person asking is not at
that machine; `gui_show` puts it on their screen when they ask. Use it when the shape of
the thing - a call graph, a growth between snapshots - says more than the numbers.

## The report

`profile_report` summarizes a session in the words of its mode and names the database file.
Hand it to the user with the rows that carry the finding; the database is theirs to open with
any SQLite client or with the GUI shipped next to the server.
