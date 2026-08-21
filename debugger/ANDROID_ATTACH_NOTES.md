# Android attach notes

Living specification: everything empirically known about deploying, launching
and attaching to a .NET for Android app. Facts marked **[verified]** were
confirmed on this machine or in primary sources; everything else is
**[unverified]** until exercised by the spike/tests.

## Protocol fundamentals

- C# code on Android runs inside **MonoVM** and is **invisible to JDWP**
  (JDWP only sees the Java side). **[verified — primary docs]**
- Debugging uses the **Mono Soft Debugger protocol (SDB)**: an agent inside the
  app's Mono runtime, TCP transport, reached from the host via `adb forward`.
  **[verified — primary docs]**
- net9.0-android still uses MonoVM by default → SDB applies to the reference application.
  **[verified — App.Droid.csproj has no UseMonoRuntime=false / CoreCLR opt-in]**

## How the SDB agent gets enabled (verified 2026-08-20, emulator API 33, net10.0-android, MonoVM)

The whole mechanism is one Android system property read by `libmonodroid` at
**process start** (`monodroid-debug` log tag):

```
debug.mono.extra = debug=127.0.0.1:<port>,timeout=<unix-seconds>,loglevel=<n>,server=y
```

- `monodroid` turns it into
  `--debugger-agent=transport=dt_socket,loglevel=<n>,address=127.0.0.1:<port>,server=y,embedding=1,timeout=30000`.
  **[verified — logcat `monodroid-debug: Trying to initialize the debugger with options: ...`]**
- `server=y` → the **app listens** on `<port>` inside the device; the host
  reaches it through `adb forward tcp:<port> tcp:<port>` and **connects**
  (`SoftDebuggerConnectArgs`, not Listen). **[verified — end to end with SdbProbe]**
- The agent's own `timeout=30000` (ms) is how long the runtime waits for the
  debugger to connect at startup. While waiting the app is suspended (no
  managed code runs — no `Tick` logcat lines until the debugger connected).
  **[verified]** What happens when nobody connects within 30 s: see
  "Agent timeout" below.
- `timeout=<unix-seconds>` in the property is a **freshness deadline**, not a
  wait time: if the device clock is already past it when the process starts,
  monodroid logs `Not starting the debugger as the timeout value has been
  reached; current-time: N; timeout: M` and runs the app **without any agent**.
  Then nothing listens on the port; the host-side `adb forward` still accepts
  the TCP connection and the client fails with `DWP Handshake failed`.
  **[verified — first run failed exactly like this]**
- Consequence: **late attach to an already-running app is impossible** — the
  property is only consulted at process start. Attach always means
  "set property → (re)start the process → connect". **[verified]**
- `loglevel=10` makes the agent chatty in logcat (`monodroid-debug`,
  `debugger-agent`); `loglevel=0` is what msbuild uses. **[verified]**
- The property persists across app restarts until overwritten (`setprop`
  survives force-stop). A stale property is harmless only thanks to the
  freshness deadline. **[verified]**

### Properties honored by libmono-android (strings of `libmono-android.debug.so` 36.1.43)

`debug.mono.connect`, `debug.mono.debug`, `debug.mono.env`, `debug.mono.extra`,
`debug.mono.gc`, `debug.mono.gdb`, `debug.mono.log`, `debug.mono.max_grefc`,
`debug.mono.profile`, `debug.mono.runtime_args`, `debug.mono.soft_breakpoints`,
`debug.mono.timing` (release runtime lacks `connect`/`env`/`soft_breakpoints`).
Agent templates: `--debugger-agent=transport=dt_socket,loglevel=%d,address=%s:%d,%sembedding=1,timeout=30000`
and `--debugger-agent=transport=socket-fd,address=%d,embedding=1` (fd handed
over by the `debug.mono.connect` path — "XS" connect mode, where the app
connects to the IDE; `Debug::start_connection/process_connection`). Unexplored.
**[verified: strings only]** All properties are device-global and read at
process start — there is no per-package or per-process scoping.

### Multi-process apps (verified 2026-08-20 with TestTarget `:helper` service)

Every process of the package (main, `:helper`, ...) reads the **same**
`debug.mono.extra` at its own start and tries to listen on the same port.

- Same port in two live processes → the second logs
  `E mono: debugger-agent: Unable to listen on 67: Invalid argument` and
  **dies immediately**; a `Sticky` service gets respawned by Android every
  2–4 s and dies again, for as long as the property is fresh. The main
  process is unaffected. **[verified — experiment A]**
- **Port rotation works** and is the engine's strategy: **[verified — experiment B]**
  1. `setprop debug.mono.extra debug=127.0.0.1:P0,...`, `adb forward` P0, `am start`.
  2. Wait for the main process to have read the property: logcat line
     `monodroid-debug: Trying to initialize the debugger ... address=127.0.0.1:P0`
     from the new pid (≈250 ms after `am start` on the emulator).
  3. Immediately rewrite the property with P1 (same deadline) and forward P1.
  4. Connect session #1 to P0 — the main process resumes and spawns helpers;
     each helper reads P1, listens, waits up to 30 s.
  5. Detect new processes of the package (new pid in the logcat line above,
     which also tells the port; `ps -A | grep <pkg>` gives the process name),
     connect session #2 to P1, rotate to P2, and so on.
  Helpers that start *before* the rotation in step 3 cannot happen for
  processes spawned by managed code (main is suspended until step 4), only
  for processes Android starts on its own (boot receivers, exported services
  hit by other apps) — handle those as "connect or die" cases.
- **The property is device-global, so it is read by every Mono app process
  that starts while it is fresh — not only ours.** Seen 2026-08-20: the reference application's
  `App.Droid:crash_report_process` (auto-started by a boot receiver while the
  TestTarget suite ran) read the property, took the next port and waited for
  a debugger; without a connection it dies after 30 s and, being a sticky
  service, respawns until the deadline passes. The engine now attaches only
  processes whose name matches the debuggee package (`ps` lookup when
  ActivityManager did not announce the pid), logs a WARNING for foreign
  processes and still rotates the burnt port. Keep the freshness deadline
  short (engine default 3 min, `LaunchOptions.PropertyLifetime` /
  `launch_app propertyLifetimeSeconds`) — this is also why the SDK's
  `RunActivity` uses a tiny deadline. **[verified]**
- A process started by **Android itself** (a manifest-declared receiver or
  service in its own `android:process`) is attached the same way, even while
  the app's main process sits suspended at a breakpoint — the main process is
  not involved in starting it. Verified with a broadcast receiver in `:late`
  (TestTarget). Note `adb shell am broadcast` blocks until the receiver
  returns, so with a breakpoint inside the receiver the adb command hangs
  until you resume: fire it without waiting. **[verified]**
- Each process is a fully independent SDB session: own threads, assemblies,
  breakpoints (pending breakpoints for a file resolve in whichever process
  loads the assembly). Locals/backtrace read fine in both concurrently.
- Detaching from a helper kills it too; Android respawns it and it waits for
  a debugger again on the port it read. End of session must therefore be:
  clear the property, then `am force-stop <pkg>`.

### Agent timeout (nobody connects)

If no debugger connects within the agent's 30 s: `E mono: debugger-agent:
Timed out waiting to connect.` → the **process exits** (`ActivityManager:
Process ... has died`) → Android **respawns** the foreground activity → the new
process reads the still-fresh property and waits again → loop until the
freshness deadline passes. Keep the deadline short (a few minutes) and reset
the property (`adb shell "setprop debug.mono.extra ''"`) when a session is
abandoned. **[verified]**

### Detach / disconnect

After the client called `Detach()` (→ `vm.Detach()`) and disposed the session,
the app process was gone immediately (no further `Tick` output, `pidof`
empty). The Mono agent (no `keepalive`) terminates the process when the
debugger disconnects — same as the classic "stop debugging kills the app"
behavior. Treat detach == terminate for now; whether a pure `Detach()` without
`Dispose()` behaves differently is untested. **[verified: process dies]**

### Manual recipe (what the engine's AndroidLauncher must do)

```bash
# one-time deploy (fast deployment, Debug) — any of:
dotnet build <Project>.csproj -t:Install -p:Configuration=Debug
# per debug session:
ACT=$(adb shell cmd package resolve-activity --brief <package> | tail -1)   # pkg/crc64....MainActivity
T=$(( $(adb shell date +%s) + 600 ))          # use the DEVICE clock, not the host clock
adb shell am force-stop <package>
adb shell setprop debug.mono.extra "debug=127.0.0.1:10000,timeout=$T,loglevel=0,server=y"
adb forward tcp:10000 tcp:10000
adb shell am start -n "$ACT"
# then connect Mono.Debugging.Soft to 127.0.0.1:10000 within 30 s
```

Verified with `DevTools/SdbProbe` against TestTarget: connect → assemblies
listed → pending breakpoint resolved (`MainActivity.cs:45` →
`void TestTarget.MainActivity.Tick () [0x00059]`) → hit on a thread-pool
thread → threads, 15-frame backtrace (2 user frames with source, 13 external),
locals `this`, `message`, `now` read with correct values → `Continue` → second
hit → `Detach`. **[verified]**

### msbuild `Run` target (what the SDK does, `Xamarin.Android.Common.Debugging.targets`)

```bash
dotnet build <Project>.csproj -t:Run -p:Configuration=Debug \
  -p:AndroidAttachDebugger=true -p:AndroidSdbHostPort=10000 -p:AndroidSdbTargetPort=10000
```

- `_Run` does `adb forward tcp:$(AndroidSdbTargetPort) tcp:$(AndroidSdbHostPort)`
  (note: the *Target* port ends up on the host side — naming is inverted with
  respect to adb semantics; irrelevant while both are equal, default 10000),
  then the `RunActivity` task (Xamarin.Android.Build.Debugging.Tasks.dll) sets
  `debug.mono.extra` with `server=y`, `loglevel=0` and a freshness deadline,
  and starts the activity. `AndroidDebuggerServer` defaults to `True`.
  **[verified — read from the installed targets, 36.1.43]**
- On this machine the first `Run` with attach **failed**: the deadline written
  by `RunActivity` was already 5 s in the past when the (cold, first-ever)
  process start reached monodroid, although host and emulator clocks agree to
  the second. The deadline the task uses is evidently very short. **Do not rely
  on the msbuild `Run` target for attaching**; use it (or `-t:Install`) only
  for deploy and set the property ourselves. **[verified]**
- Reference DAP client that drives the same mechanism: `vscode-mono-debug`
  (`"type": "mono", "request": "attach", "address": "localhost", "port": 10000`).
  MIT, usable as a protocol reference. **[unverified locally]**

### Mono.Debugging.Soft client side (verified)

- `new SoftDebuggerSession()`; set `ExceptionHandler` (mandatory — exceptions
  on the event thread are routed there), `LogWriter`, `OutputWriter`.
- `session.Breakpoints.Add(file, line)` before `Run` works: the breakpoint is
  pending and resolved when the assembly loads (`LogWriter` reports
  `Resolved pending breakpoint at ...`). File path matching is against the
  absolute path embedded in the portable PDB (here the Windows build path).
- `session.Run(new SoftDebuggerStartInfo(new SoftDebuggerConnectArgs(appName, IPAddress.Loopback, port)), new DebuggerSessionOptions { EvaluationOptions = EvaluationOptions.DefaultOptions })`.
  Connect + handshake took ~150 ms on the emulator; assemblies stream in as
  `AssemblyLoaded` events right after `TargetReady`.
- `TargetHitBreakpoint` carries `e.Thread` and `e.Backtrace`; frames expose
  `SourceLocation` (`MethodName`, `FileName`, `Line`), `IsExternalCode`,
  `HasDebugInfo`; `frame.GetAllLocals()` returns `ObjectValue` (`Name`,
  `TypeName`, `Value`, `DisplayValue`, `IsEvaluating`/`WaitHandle`).
  `session.GetProcesses()[0].GetThreads()` lists threads with `Location`.
- Runtime dependency trap: `Mono.Debugger.Soft.csproj` references
  `Mono.Cecil 0.10.1` with `PrivateAssets="all"`, so it is **not** copied to
  consumers, but `MethodMirror.GetCustomAttributes` (used by
  `SoftDebuggerBacktrace.CreateStackFrame`) needs it at runtime →
  `FileNotFoundException: Mono.Cecil` on the first breakpoint hit, surfaced as
  `DisconnectedException`. Every consumer project must reference
  `Mono.Cecil 0.10.1` itself (Core and SdbProbe do). **[verified]**
- Method invocation in the debuggee (property getters, `ToString`, debugger
  type proxies) runs on the stopped thread. When an invoke exceeds
  `EvaluationOptions.EvaluationTimeout` the agent logs `Aborting invocation of
  method ...`; if the abort cannot interrupt it (seen with the first
  `DateTime.ToString()` of a process = culture/ICU initialization, several
  seconds on a software-GPU emulator) **that thread stays wedged: every later
  invoke on it fails or times out** (`sample.Map.Count` → "could not evaluate",
  dictionary proxy expansion → 20 s timeout) and, worst case, the synchronous
  Mono.Debugging call never returns. Engine mitigations: generous defaults
  (`EvaluationTimeout` 6 s, `MemberEvaluationTimeout` 10 s, tunable via
  `SetEvaluationOptions` / MCP `set_evaluation_options`, including
  `AllowToStringCalls=false` for slow targets) and `RunBounded` (45 s) so a
  stuck invoke costs a leaked thread instead of a hung frontend.
  **[verified — suite runs 7 and 9, live MCP reproduction 2026-08-20]**
  What happens to the *debuggee* after such an abort is not deterministic: an
  invocation blocked in a call the abort cannot interrupt (`Thread.Sleep`, a
  native call) sometimes survives and the thread keeps working, and sometimes
  the runtime keeps retrying the abort until the process dies. Prefer generous
  timeouts, and on targets where a getter may block prefer
  `allowTargetInvoke=false` over relying on the abort. **[both outcomes
  observed on the same test, 2026-08-20]**
- Debuggee traces (`Debug.WriteLine`, `Console.WriteLine`, app loggers) reach
  the client as SDB **UserLog** events; `SoftDebuggerSession` hands them to
  `DebuggerSession.DebugWriter(level, category, message)` and, when that is
  not set, folds them into `LogWriter` as `[level:category] message` (the
  `[0:] ...` lines seen with the reference application). Set `DebugWriter` to keep app output
  separate from debugger log. **[verified]**
- SDB protocol version spoken by the net10 Android agent is accepted by
  debugger-libs `e7fbb713` unmodified. **[verified]**

## Requirements on the app build

- Debug configuration: debuggable app, fast deployment, Mono debug agent
  enabled. Release APKs are not debuggable. **[verified — toolchain docs]**

## Licensing constraint

The VS Code ".NET MAUI" extension's debug adapter is part of the proprietary
C# Dev Kit family (license bound to VS Code). Do not drive it from our code and
do not reuse its binaries. Open alternatives: `mono/debugger-libs`,
`microsoft/vscode-mono-debug` (both MIT). **[verified — C# Dev Kit FAQ]**

## This machine

- adb 1.0.41 (36.0.0) at `C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe` (on PATH)
- Emulator AVD: `pixel_7_-_api_33_0`
- .NET SDK 10.0.301, workloads: android 36.1.43 (via VS 18.7), ios, maccatalyst, maui-windows
- No desktop Mono runtime installed
- Android packs installed: `Microsoft.Android.Sdk.Windows` 36.1.43 (and
  35.0.105), `Microsoft.Android.Ref.36`, Mono/CoreCLR/NativeAOT runtime packs
  for 36 only. `UseMonoRuntime` defaults to `true` in 36.1.43 → net10.0-android
  apps still run on MonoVM unless they opt in to CoreCLR/NativeAOT. **[verified]**
- Emulator: `emulator.exe -avd pixel_7_-_api_33_0 -port 5554 -gpu host -cores 4`
  (x86_64, API 33, hw.ramSize=1536 in the AVD config); adb serial
  `emulator-5554`; boot to `sys.boot_completed=1` in well under a minute with
  snapshot, ~1 min cold. Host and emulator clocks agreed to the second.
  Stop it with `adb -s emulator-5554 emu kill` (targeted by serial; killing
  the qemu PID from a non-elevated shell silently fails and a second launch
  then refuses to start: "multiple emulators with the same AVD").
  **Unattended runs must be headless**: a windowed emulator cannot start once
  the desktop session is locked or the display sleeps — it logs `Unable to
  open monitor interface to \\.\DISPLAY1`, never registers with adb, and just
  hangs. `-no-window -gpu host` starts fine in that state and keeps the
  hardware GPU, which matters: under `swiftshader_indirect` the debuggee is
  slow enough that ordinary property getters exceed the evaluation timeout,
  and the resulting abort storm (dozens of `Aborting invocation of ...` for a
  single getter) can kill the app.
  `DevTools/scripts/ensure-emulator.sh` defaults to that and also clears the
  stale `hardware-qemu.ini.lock` / `multiinstance.lock` / `read-snapshot.txt`
  a crashed instance leaves behind (clearing them does not touch installed
  apps or user data; a corrupted `snapshots/default_boot` also blocks boot,
  hence `-no-snapshot`).
  GPU: `-gpu host` (NVIDIA GL) is fast but crashed qemu twice in one day;
  `-gpu swiftshader_indirect` is stable but so slow that debuggee invokes
  time out and the suite becomes flaky (runs 10-11); `-gpu angle_indirect`
  silently falls back to SwiftShader here. **[verified 2026-08-20]**

## TestTarget (validation app, `TestTarget/`)

- `net10.0-android`, `ApplicationId=net.androiddebugger.testtarget`, MonoVM,
  fast deployment (assemblies land in
  `/data/data/<pkg>/files/.__override__/x86_64/`). **[verified]**
- Launcher activity Java name is the crc64 one:
  `net.androiddebugger.testtarget/crc647005ae1102f9eb2b.MainActivity` — resolve
  it with `adb shell cmd package resolve-activity --brief <pkg> | tail -1`
  instead of hardcoding. **[verified]**
- Breakpoint fodder: `MainActivity.Tick()` (1 s timer on a thread-pool thread,
  locals `now`, `message`) and `OnIncrementClicked` (UI thread, button).
- Deploy: `dotnet build TestTarget/TestTarget.csproj -t:Install -p:Configuration=Debug`
  (first build ≈ 1–2 min). **[verified via `-t:Run`]**

## Real target: the reference application

- Solution: `C:\Work\ReferenceApp\the reference application.sln` (branch `master`)
- App project: `App.Droid\App.Droid.csproj`, TFM `net9.0-android35.0`,
  `ApplicationId=App.Droid`
- Companion projects: App.Core, App.Shared, App.Sync, App.DroidLibs, …
- Old checkout at `C:\Work\stable` is classic Xamarin (2022) — ignore it.
- **Multi-process (verified in source 2026-08-20):**
  - `App.Droid/Services/ForegroundService/CrashReportSender.cs`:
    `[Service(Name="the app's crash reporting service", Process=":crash_report_process")]`
    → process `App.Droid:crash_report_process`. Started by
    `AppApplication.InizializzaApplicazione()` right at app init (main process
    only, guarded by `IsMainProcess()`), by `BootReceiver`, and after a bug
    report is prepared. Runs `SendPendingCrashReports()` (loop with 1-minute
    `Task.Delay`), then `StopSelf()`; `Sticky`. So the helper process is
    alive from the first seconds of every launch → port conflict with the
    main process is the **normal case**, not an edge case.
  - **`am force-stop` returns before the processes are gone.** Publishing the
    port while one of them is still alive (or while Android is bringing a sticky
    service back up) hands it the property, and the process we actually want
    loses its agent — the symptom is `no SDB handshake on port N within 20s`
    followed by the app dying. The launcher now waits (up to 8 s) for the
    package to have no processes left before writing the property, and warns if
    something refuses to die. Found 2026-08-21 by full-suite runs where the
    victim changed every time; single tests never showed it, because the
    leftovers come from the *previous* test.
  - **Port rotation happens at process start, not at agent init (2026-08-21).**
    ActivityManager's `Start proc <pid>:<name>/<uid>` line comes at fork, long
    before the runtime reads `debug.mono.extra`; rotating there shrinks the
    window in which two processes read the same port to almost nothing. Before
    this, a process starting while another was between fork and agent init took
    the same port, could not listen, and died — the visible symptom being
    `no SDB handshake on port N within 20s`. Rotation is idempotent, so the
    second call (at agent init, which reports the port actually taken) costs
    nothing. See KNOWN_UNKNOWNS U14.
  - **Shutdown order (2026-08-21):** the logcat reader is what rotates the
    property, so it must be stopped *before* the property is cleared. Clearing
    first leaves the device carrying a stale `debug.mono.extra` whenever Android
    is still restarting one of the app's processes, because the reader publishes
    a fresh port after the clear. A flag also makes rotation a no-op once
    shutdown starts, for a line already being handled.
  - `the background service` (referenced by App.Droid):
    `[Service(Name="the app's background service", Exported=true, Process="the app's own android:process")]`
    → third, global-named process, on demand.
  - `TTManager` (foreground service) runs in the main process.
- **Exception type can be missing at stop time (2026-08-21):** a first-chance
  stop inside a third-party assembly without symbols (MQTTnet's
  `MqttClient.ConnectAsync`) reported an empty type; the message and stack came
  through. The engine now falls back to the `$exception` value's own type name,
  read on the caller thread. See KNOWN_UNKNOWNS U15.
- **Null locals of an async frame lie about having children (2026-08-21):**
  locals a method has not reached yet are fields of the state machine, so they
  are visible and null; Mono reports them as `(null)` *without* its null flag
  and claims they have children. Expanding one returns nothing. The engine now
  treats a value rendered as `(null)` as null and gives it no expansion handle.
- **Third process debugged for real (2026-08-21, republished server):** with
  `keepPropertyFresh`, `adb shell am start-foreground-service -n
  App.Droid/the app's background service` starts `the app's own android:process` (uid 10174,
  same as `App.Droid`), the launcher recognises it by uid and attaches it on port
  10002 alongside the main process and `:crash_report_process`. A breakpoint in
  `the background service` `OnStartCommand` is hit there, with its
  stack (through the JNI marshal frames) and its locals — `intent`, `startId`
  and `this` — readable. This is the process Visual Studio cannot debug at all.
  **[verified]**
- **Late processes and the deadline (2026-08-21):** the `timeout=` field is a
  device-epoch instant, and a process reading the property after it has passed
  starts without a debugger — silently, from the debugger's point of view.
  Measured with a 25 s lifetime: a process spawned 40 s after launch is running
  on the device (`ps` shows it) and is simply not attached. `KeepPropertyFresh`
  rewrites the property every `lifetime/3` (at least every 20 s) for as long as
  the session lives, and the same process is then attached on its own port.
  Off by default: the freshness that helps our late processes is the same
  freshness that makes an unrelated Mono app stall waiting for a debugger on
  our port. Turn it on for apps with on-demand services (the reference application's
  `the app's own android:process`, its crash reporter). **[verified]**
- **Processes of the app are identified by uid, not only by name (2026-08-21):**
  a component declared with a global `android:process` (the reference application's
  `the background service` → `the app's own android:process`) runs in a
  process whose name shares nothing with the package. The launcher resolves the
  package uid once per launch (`pm list packages -U <pkg>` → `uid:<n>`) and
  falls back to comparing it against the process's uid (`ps -A -o PID,UID,NAME`)
  before deciding a Mono process that took our port is foreign. Apps that share
  a uid would match too, which is intended: they share the sandbox. **[verified]**
- **Long debugger pause (U6, measured 2026-08-20):** stopped at a breakpoint
  on **thread 1 (the UI thread)** and held for **4.5 minutes** on the
  emulator, foreground:
  - Both processes survive the whole pause: no ANR, no `has died`, no
    lowmemorykiller, no "Application Not Responding" — Android does not kill
    an app whose managed threads are all suspended by the debugger, as long
    as nothing demands input from it. **[verified]**
  - Repeated on a **fully operational installation** (user logged on, talking
    to the backend, MQTT up), suspended with an explicit pause for **5
    minutes** and then resumed, watching for 6.5 minutes afterwards:
    - Both processes stayed alive throughout; no ANR, no death.
    - MQTT: exactly one `ManagedMqttClient.ReconnectIfRequiredAsync` failure
      ~3 s after the resume, then silence — `MQTTnet`'s managed client
      (`WithAutoReconnectDelay` 5 s) reconnects on its own and does not enter
      a reconnect storm.
    - The sync **watchdog never fired**: no "Thread riavviati forzatamente",
      no "PREPARING BUGREPORT", no thread restart, and no bug-report file in
      the app's data directory — even though its `DevoRiavviareIThread`
      thresholds are wall-clock based (`the sync library's watchdog`:
      60 s loop, 2-minute thresholds, 4 consecutive iterations). The threads
      resume work immediately, so their timestamps refresh before the
      consecutive-iteration counter can build up.
    **[verified 2026-08-20]** Pauses of a few minutes are therefore safe on
    the reference application. Much longer pauses (tens of minutes) were not measured; the
    watchdog can in principle call `PrepareBugReport(...).Send()` and restart
    every sync thread, so a real bug report from the device stays the failure
    mode to watch for.
- **Debugged through the MCP server (2026-08-20):** attach (= restart with
  agent) works on the installed Debug build (fast deployment, assemblies in
  `.__override__/x86_64`); PDB paths are the `C:\Work\ReferenceApp\...` sources,
  so breakpoints on those absolute paths bind. Verified: breakpoint on startup
  code in the main process (`AppApplication.InizializzaApplicazione`, set
  before launch), breakpoint in another assembly (`App.Core.dll`, pending until
  loaded, then hit on the main thread id 1), helper `:crash_report_process`
  auto-attached on the next port, expansion of `this` with Unity container /
  lists, step over, terminate leaves the device clean. **[verified]**
- **Re-driven through the MCP server after the breakpoint-disarm fix
  (2026-08-21):** the scenario the fix targets — a breakpoint on periodic
  background code while inspection invokes debuggee code — was exercised
  against a fully started the reference application. Breakpoint on
  `the sync library's base thread:112`
  (`AttendiMillisecondi`, the wait every sync thread goes through: WatchDog
  every 60 s, Sender every 500 ms when it has just polled). Result over five
  stops on three different sync threads, with deep expansion in between
  (`WatchDog` / `Sender` graphs, `ThreadsManagerImpl`, `DatabaseImpl`, the
  Unity container, plus `evaluate_expression` reaching across objects):
  no freeze, no aborted invocation, no lost process. Also observed live:
  - the async frame reports the user method on top with the state-machine
    frames marked `[external]`, and `step_over` walks the user lines;
  - `:crash_report_process` **ended and was restarted by Android during the
    session**; the dead one is reported `exited` and the new one is attached
    on the next free port (10001 → 10002) without touching the main process;
  - suspending the app makes MQTT time out — the app logs a handled
    `MqttCommunicationTimedOutException` and an `InvalidOperationException`
    ("Not allowed to connect while connect/disconnect is pending") and
    reconnects by itself, consistent with the 5-minute pause measurement
    above.
  **[verified]**
- **Device clock vs host clock (2026-08-21):** the emulator runs in GMT while
  this host is GMT+2. logcat's `threadtime` stamps are **device local time**,
  so app output that reaches us through the debugger (stdout/stderr on the SDB
  user-log channel) must be shifted onto the same clock or the two channels
  interleave hours apart in one listing. The launcher now measures the offset
  once per launch (`AndroidLauncher.DeviceClockOffset`, from
  `adb shell date "+%Y-%m-%d %H:%M:%S"`) and every timestamp the session
  reports is on the device clock. **[verified]**

## Sources

- https://docs.avaloniaui.net/docs/guides/platforms/android/configure-vscode-debug-linux
- https://learn.microsoft.com/en-us/dotnet/android/building-apps/build-properties
- https://code.visualstudio.com/docs/csharp/cs-dev-kit-faq
- https://github.com/mono/debugger-libs
- https://github.com/microsoft/vscode-mono-debug
