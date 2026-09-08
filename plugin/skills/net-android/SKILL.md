---
name: net-android
description: Operating guide for the net-android MCP server, the debugger and profiler for .NET for Android and .NET MAUI Android apps (MonoVM). Use it whenever a task involves a slow screen, a slow operation, memory growth, allocation churn, a crash, an exception, a wrong value, a breakpoint, a profiling session, or driving an Android app on a device or emulator through the server's tools (list_devices, build_app, profile_run, launch_app, get_ui_hierarchy and the rest).
---

# Working with the net-android server

One MCP server, two engines on one device layer: a **debugger** (Mono soft debugger:
breakpoints, stepping, locals, exceptions) and a **profiler** (CPU sampling, instrumenting,
heap snapshots), plus **screen tools** that drive the app through adb. This file is the
operating knowledge; the tool descriptions are the reference for parameters.

## Ground rules

1. **Always pass `deviceSerial`.** Never rely on adb's default device: with two devices every
   tool that guesses fails, and a tool that picks wrong touches the wrong app.
2. **One device runs one engine at a time**, with one exception: the profiler may attach to the
   very process the debugger is running (see "Debugger and profiler on one process"). A call
   that would start the second engine is refused, and the refusal names the session to stop.
3. **When a resource is blocked, stop and ask the user.** Port 9000 in use, no device online,
   a dsrouter left over, the app not installed, an emulator that will not boot: report the exact
   message and wait. Never work around it with shell commands of your own.
4. **Finish every session**: `profile_stop` for what `profile_start` began, `stop_debugging` for
   every debug session, and say what was left on the device (an app stopped, a property cleared).
5. **Evidence before conclusions.** A hotspot with a handful of samples, a timing from three
   calls, a heap diff over five seconds: not evidence. Lengthen the session or repeat it.
6. **Confirm a fix the same way the problem was measured**: the same mode, the same
   scenario, then compare the two sessions by `sessionId`.

## Preflight, in this order

1. `list_devices` → pick the serial; `check_device_control` says whether screenshots, the UI
   hierarchy and input injection work on it (some vendors need a developer setting).
2. `list_app_projects` on the sources folder, solution or project: package name, `symbolsDir`
   (the build output with the pdbs) and the assemblies a callspec lives in. Pass `symbolsDir`
   to every profiling call: it is what makes results name source files.
3. `check_app` on the installed package: which modes work and what is missing. Read it before
   building anything.
4. `build_app` when, and only when, `check_app` reports the mode you need as not available,
   the app must change deployment shape, or the sources changed. One purpose word:
   `debug`, `sampling`, `heap`, `instrumenting` are all the same build (Debug, diagnostics on,
   fast deployment), so one installed app serves the debugger and every on-device profiling
   mode; `instrumenting-build-time` is for an app that must keep its assemblies inside the APK.
   Details: [references/build-matrix.md](references/build-matrix.md).
5. Nothing else may start the app while a session holds the device: not the IDE, not the user.

## Choose the approach by the question

| The question | Start with | Then |
|---|---|---|
| The app or a screen is slow, nobody knows where | `profile_run` sampling (attach if the app is up; `launch: restart` with `suspendOnStart` to catch startup) | `profile_hotspots`, `profile_tree`, `profile_callers` |
| One operation is slow and you know roughly where | sampling to find the namespace | `profile_run` instrumenting, `engine: weaver-tree`, `callspec` on that namespace or type; `profile_timings` |
| Call order, or every single call's duration | — | instrumenting with `engine: weaver`; `profile_timings`, `profile_tree` |
| Too many allocations, GC pauses | instrumenting with `trackAllocations: true` | `alloc_report`; `bySite: true` needs `engine: weaver` |
| Memory grows and never comes back | `profile_run` with `mode: heap`, `snapshots: 2`, an interval long enough for the growth | `heap_diff`: the types at the top are the candidates |
| A crash, an exception, a wrong value | the debugger: `launch_app`, exception rules, breakpoints | `get_locals`, `evaluate_expression`, `get_exception_details` |
| "What happens after this point" | the debugger to the point, then sampling attached to that process | see "Debugger and profiler on one process" |

**Escalate, do not jump.** Each mode's output is the next mode's input: hotspots name the
namespace for a callspec; timings name the method for a breakpoint; locals give the hypothesis;
the confirmation run is the first mode again. Switch mode when the current one cannot answer
the question, not because it is inconvenient. Sampling is cheap and safe on any app; instrumenting
costs per call and needs a narrow callspec; the debugger stops the app.

## Running sessions

- `profile_run` is one shot: it prepares the device, collects for `durationSeconds`, analyzes
  and returns a summary. `profile_start` … `profile_stop` when the user must do something in
  the app meanwhile, or the length is unknown. Read-only tools default to the last session;
  pass `sessionId` when comparing.
- Durations: 15-30 s for sampling a scenario; long enough to cover the operation once or twice
  for instrumenting; heap intervals of minutes when hunting a leak.
- `maxTraceMb` guards a real app: sampling produces about 1.5 MB per second on a large app, and
  a session without a duration is the one that runs away.
- `keepAppRunning` defaults to false on a restart session, and should stay so: an app started
  for profiling keeps reconnecting to the profiler's port until it is stopped, and would be
  profiled instead of the next target.
- **Attach** (`launch: attach`) needs an app that was started with a diagnostics port (a build
  with diagnostics enabled has one). Sampling and heap attach; instrumenting never does, it
  restarts the app. Same process id before and after.
- Heap snapshots never suspend the app, whatever `suspendOnStart` says.
- Instrumenting: `callspec` is mandatory and must be narrow (`T:` one type, `N:` a small
  namespace). A whole application namespace can keep the app from ever starting. Keep hot
  leaf methods out of it: the cost is per call. `engine: weaver-tree` (default) records a call
  tree in the app, cheapest; `engine: weaver` records every call, for order and per-call
  durations; the runtime `provider` engine crashes .NET 9 apps, never choose it there.
- Live control on weaver sessions: `profile_snapshot` (results so far, app keeps running),
  `profile_pause` / `profile_resume`, `profile_clear`, `profile_archive` (keep a named copy of
  the results and go on; `profile_archives` lists them, any read-only tool opens one).

## Reading results

Details and worked readings: [references/reading-results.md](references/reading-results.md).

- Samples are ~1 ms each. `inclusive` = the method was on the stack, `exclusive` = it was on
  top. `*_cpu` columns drop samples of threads blocked in waits; use them for CPU questions and
  the plain ones for "where does the time go".
- **A method's exclusive samples include its tiny callees**: the MonoVM sampler does not
  report very short leaf methods. Read every hotspot as "this method plus its trivial callees".
- No per-line samples on MonoVM: `profile_annotate_source` puts a method's figures on its
  first line, with the range marked. It needs `symbolsDir`.
- AOT-compiled methods hide their leaves in the caller. Profile JIT builds for attribution.
- Instrumenting figures are exact: calls, total, self, min, max. `(async body)` rows are the
  resumptions of an async method, their time excludes the awaits; `(iterator body)` rows count
  one call per item produced. Without the body rows an async method's own work is invisible.
- `profile_threads` tells which thread the time belongs to; a hot background thread and a
  frozen UI thread are different bugs.
- `profile_report` last: it is the summary to hand to the user, and it names the database.

## Debugging

- `launch_app` restarts the app with the debugger agent; `launch_from_config` reads a
  `.vscode/launch.json` so an app's launch settings live with its sources. There is no attach to
  a running process on Mono, and **detaching terminates the app**: `stop_debugging` is the only
  end. `deploy: true` builds and installs first when the sources changed.
- Set breakpoints before `launch_app` to catch startup code (`OnCreate`, `Application` ctor).
- **A real app throws on purpose** (network timeouts, handled retries). Before the first
  `continue_and_wait`, install `set_exception_rules`: ignore the noisy types, log the handled
  ones, break on the rest. `use_global_exception_rules` keeps them in a file that is re-read on
  every resume. Plain `set_exception_filters` alone stops every few seconds on a busy app.
- Prefer a logpoint (`set_breakpoint` with `logMessage`) to a stop on an app that talks to a
  backend: a suspended app makes its server time out and reconnect.
- `wait_until_stopped` returns the current stop; `continue_and_wait` resumes and waits for the
  next one. Every stop reports pid and thread; helper processes of the app attach on their own.
- `get_compact_debug_snapshot` gives location, stack and locals in one call. Reading a property
  runs code in the stopped thread: on a slow emulator `set_evaluation_options` with generous
  timeouts, or `allowTargetInvoke: false`, avoids wedging that thread.
- The debug property the launcher sets is device-global for `propertyLifetimeSeconds`: any other
  Mono app starting in that window waits for a debugger. Keep it short; no second debugger
  (an IDE) on the same device.

## Debugger and profiler on one process

Run to the point of interest under the debugger, then sample what follows, in the same process:

1. `set_breakpoint` at the threshold, `launch_app`, `wait_until_stopped`.
2. `remove_all_breakpoints` and `set_exception_rules` with `[]`: nothing may stop the app while
   it is sampled.
3. `profile_start` with `launch: attach`, the same `packageName` and `deviceSerial`. The runtime
   connects while the app is still stopped, so collection is under way before it resumes.
4. `continue_and_wait` with a timeout that covers the operation; the answer is "timeout", which
   is what you want.
5. `profile_stop`, then `profile_hotspots` / `profile_tree`. The debug session is still alive:
   set breakpoints again and go on.

Only this combination is allowed on one device. A restart of the app for profiling while the
debugger holds it is refused; `stop_debugging` first. Instrumenting the debugger's app is not
possible (it restarts the app). The figures describe code compiled with the debugger attached,
which for a Debug build is what it always runs.

## Driving the app's screen

Details: [references/ui-driving.md](references/ui-driving.md).

- `get_ui_hierarchy` first: it is structure (ids, texts, descriptions, bounds), cheap to read and
  what `tap_screen` selectors match. `capture_screenshot` for visual state, and it is the one
  screen tool that works while the app is suspended at a breakpoint.
- **MAUI apps have no Android resource ids.** Their controls show `id=` empty; select by
  `text`, `contentDescription` (a MAUI `AutomationId` lands there) or `className`. Native
  apps expose the ids declared in `Resources/layout/*.xml` as `<package>:id/<name>`.
- **Correlate the screen with the sources** before acting: hierarchy first, then the page that
  owns it (a MAUI `.xaml` page or Shell route; a native layout file and its Activity), so taps
  and breakpoints target what the code names. Read both kinds of layout when both exist
  (a MAUI app with native views, a native app with a MAUI page).
- Input tools are refused while the app is suspended: resume first. `type_text` is printable
  ASCII only, `%s` for a space; `press_key` for ENTER, TAB, BACK. Some emulator images crash
  the on-screen keyboard when a field gets focus: type, then `press_key` BACK, and tell the
  user if the keyboard loops.
- `wake_screen` before anything on a device that went dark: an off screen has no UI hierarchy.

## Showing results, not only describing them

Some findings are a picture: a call graph, the growth between two heap snapshots, a hot
method with its figures beside the source. The profiler's GUI draws them on request.

- `gui_open` (a session id, a panel) starts the GUI **without showing a window**, `gui_view`
  chooses the panel and focuses a method, `gui_capture` returns the panel as a PNG in the
  answer. Nothing appears on anyone's screen and nothing of their desktop can end up in the
  picture: the GUI paints itself. `gui_close` when done.
- That is the default because the person asking may not be at that machine. `gui_show` puts
  the window on their screen only when they ask for it ("open it", "show me on screen").
- A written report is worth a page with the pictures in it: capture what carries the finding
  (`savePath` keeps a large PNG out of the conversation), then build the report around them,
  each picture next to the numbers it illustrates and the session id it came from.
- Panels: `report` (the table, with its percentage bars), `tree` (the call tree), `graph`
  (callers and callees around the focused method), `source` (the figures beside the code,
  needs symbols), `memory` (allocations and heap growth), `summary`, `monitor`, `log`.
  `target: window` draws the whole window instead of one panel.
- The GUI ships with the server. Where it is missing, say so and report in words: it is a
  convenience, never a prerequisite for an answer.

## Traps, one line each

Full list with symptom, cause and remedy: [references/traps.md](references/traps.md).

- One profiler router per machine, on port 9000. In use means: stop and ask.
- An app profiled with a restart keeps reconnecting to the port until stopped. Stop it.
- A dirty override environment on the device (left by an interrupted session) breaks the next
  launch silently; `check_app` and the session log say so; a reinstall clears it.
- Switching an app between embedded assemblies and fast deployment leaves stale copies on the
  device: `build_app` with `clearDeployedAssemblies: true`.
- Exclusive samples include tiny callees; no per-line samples; AOT hides leaves.
- Instrumenting recording nothing after a device has been profiled for a long time: restart the
  device (or use the weaver engine, which does not depend on the runtime's instrumenting).
- A callspec too wide keeps the app from starting. Narrow it, always.
- Commas inside a callspec are fine in tool calls; the tools escape them for msbuild.
- A force-stopped app is sometimes not forked by a launch: the tools retry, do not add your own.
- The debug and profile device properties are global: another Mono app starting meanwhile is
  disturbed; two debuggers or two profilers on one device overwrite each other.
- The IDE must not start the app while a session holds the device.

## Reporting

A finding carries: the session id and mode, the device and package, the rows that are the
evidence (with their counts), the hypothesis in one sentence, and the confirmation run when the
fix is in. Say what was left on the device.
