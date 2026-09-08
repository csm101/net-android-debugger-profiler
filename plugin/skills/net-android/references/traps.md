# Traps: symptom, cause, what to do

Every item here cost somebody hours. When a symptom matches, act on the remedy before forming
any other hypothesis.

## Port 9000 is in use

- Symptom: `profile_run` / `profile_start` fail at once with "port 9000 is already in use".
- Cause: the profiler reaches the app through one router process per machine, always on
  `127.0.0.1:9000`; a previous session left one alive, another profiling tool holds it, or an
  unrelated service (a container publishing 9000 is a classic) listens there.
- Do: stop and tell the user, with the message. Do not kill processes yourself.

## The previous app keeps reconnecting

- Symptom: a session connects to the wrong app, or the engine fails naming another session's
  marker.
- Cause: an app started for profiling with a restart keeps the diagnostics port in its process
  environment and reconnects to any later router on the machine, until it is stopped.
- Do: the engine stops what it launched unless `keepAppRunning` was set; if it was, or the
  session was killed, ask for the app to be stopped (`terminate_app` if a debug session holds
  it, otherwise the user or a fresh restart session of that same app).

## Dirty override environment, or an app that no longer starts

- Symptom: the app sits on its splash screen or dies without a crash in `get_app_output`; a
  session waits for a runtime that never connects.
- Cause: a killed session left the per-app environment file with a profiling port or a
  suspend flag; or the app's fast-deployment directory holds stale copies from a previous shape
  of the build (embedded ↔ fast deployment); or a killed weaver session left assemblies or a
  pdb moved aside.
- Do: `check_app` and the session log name what they found; a `build_app` with
  `clearDeployedAssemblies: true` or an uninstall and reinstall clears all of it. The next
  weaver session repairs its own leftovers.

## Instrumenting records nothing

- Symptom: an instrumenting session ends with zero calls, or allocations without any method
  enter/leave, on a callspec that is right.
- Cause, in order of likelihood: the app embeds its assemblies (on-device weaving found no
  files: `check_app` says so; use build-time weaving); the callspec names a namespace that
  lives in a library not listed in `weaveAssemblies`; the runtime's own instrumenting provider
  was used, which crashes .NET 9 apps and degrades on a device profiled for a long time.
- Do: `engine: weaver-tree` or `weaver` (the default engines never depend on the runtime's
  instrumenting); `weaveAssemblies` from `list_app_projects`; and if a provider session on a
  long-running device recorded nothing, restart the device.

## A callspec too wide

- Symptom: the app never reaches its first screen; the session times out waiting.
- Cause: thousands of instrumented methods; the app spends minutes verifying classes.
- Do: one type (`T:`) or a small namespace; use sampling to choose it; keep hot leaves out.

## Sampling numbers that mislead

- A hot method with no callees: the sampler folds very short leaves into their caller.
- A method at 90% that is a wait primitive: the thread was blocked; read the `_cpu` columns.
- Half the samples "missing": samples without a managed stack are counted separately.
- A leaf that vanished in a Release build: AOT; profile a JIT build.
- Per-line figures: there are none on MonoVM; annotation is per method.

## Heap snapshot with no objects

- Symptom: "the snapshot produced no objects" on an app that clearly runs.
- Cause: the app was suspended at start and nothing resumed it before the dump.
- Do: heap sessions never suspend; if one did (an old server), run with
  `suspendOnStart: false` or attach to the running app.

## Attach that never connects

- Symptom: `launch: attach` fails at once saying the app has no diagnostics port.
- Cause: attach cannot give a running process a port; the app had to be started with one
  (a build with diagnostics enabled, or a restart session before).
- Do: `build_app` for the purpose, then restart the app; or a restart session.

## The launch that does not fork

- Symptom: a restart session or `launch_app` reports the app as not running right after
  starting it.
- Cause: after a force-stop Android sometimes accepts the start intent without forking the
  process; the tools retry with a second strategy and poll for the pid.
- Do: nothing of your own; read the session log, and if it failed twice tell the user (a
  device reboot has fixed it).

## The debugger's property is device-global

- Symptom: an unrelated Mono app on the same device hangs for half a minute at startup, or a
  helper process of the app loops "waiting for a debugger".
- Cause: the launch property every Mono process reads at start, valid for
  `propertyLifetimeSeconds`; a second debugger (an IDE) on the same device overwrites it.
- Do: keep the lifetime short, one debugger per device, `stop_debugging` when done (it clears
  the property). Never start the app from the IDE while a session holds the device.

## Breakpoints that never bind

- Symptom: `set_breakpoint` stays pending after the app is running; `get_source_files` reports
  no file.
- Cause: the path does not match what the pdb recorded; or the installed app carries no pdb
  (a Release build, or a killed weaver session that moved it aside).
- Do: `get_source_files` with the file name shows the paths the runtime knows; if none,
  rebuild with `build_app` (purpose `debug`) and launch again.

## Evaluation wedges the thread

- Symptom: after one slow property read every later `get_locals` on that thread times out.
- Cause: reading a property runs code in the stopped thread; an aborted evaluation is not
  reliably undone, on slow emulators in particular.
- Do: `set_evaluation_options` with generous timeouts, or `allowTargetInvoke: false` to read
  fields only; step or continue and stop again to get a fresh thread state.

## The screen tools

- `get_ui_hierarchy` fails "could not get idle state" or "null root node": the app is suspended
  at a breakpoint (resume first, or `capture_screenshot`), or the screen is off (`wake_screen`).
- Input refused: the app is suspended; resume first.
- Text typed as garbage: non-ASCII in `type_text`.
- A crash dialog every few seconds after tapping a text field: the emulator's keyboard app;
  `press_key` BACK and tell the user.
- Every tap fails with a security error on a vendor ROM: the developer setting that allows
  input injection over adb; `check_device_control` names it.

## Two devices

- Symptom: a tool acts on the wrong device or refuses to guess.
- Cause: adb's default device is whichever it lists first.
- Do: `deviceSerial` on every call, always.
