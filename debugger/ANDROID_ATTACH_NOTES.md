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
- Emulator: `emulator.exe -avd pixel_7_-_api_33_0` (x86_64, API 33); adb serial
  `emulator-5554`; boot to `sys.boot_completed=1` in well under a minute with
  snapshot. Host and emulator clocks agreed to the second. **[verified]**

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
  - `the background service` (referenced by App.Droid):
    `[Service(Name="the app's background service", Exported=true, Process="the app's own android:process")]`
    → third, global-named process, on demand.
  - `TTManager` (foreground service) runs in the main process.

## Sources

- https://docs.avaloniaui.net/docs/guides/platforms/android/configure-vscode-debug-linux
- https://learn.microsoft.com/en-us/dotnet/android/building-apps/build-properties
- https://code.visualstudio.com/docs/csharp/cs-dev-kit-faq
- https://github.com/mono/debugger-libs
- https://github.com/microsoft/vscode-mono-debug
