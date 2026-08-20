using System.Text.Json;
using NetAndroidProfiler.Core.Analysis;
using NetAndroidProfiler.Core.Apps;
using NetAndroidProfiler.Core.Collection;
using NetAndroidProfiler.Core.Devices;
using NetAndroidProfiler.Core.Store;
using NetAndroidProfiler.Core.Weaving;

namespace NetAndroidProfiler.Core.Sessions;

/// <summary>Which mechanism produces instrumenting (enter/leave) data.</summary>
public enum InstrumentingEngine
{
    /// <summary>Microsoft-DotNETRuntimeMonoProfiler callspec (MonoVM only; crashes net9 runtimes - see U20).</summary>
    RuntimeProvider,
    /// <summary>Mono.Cecil IL weaving of the app assemblies (runtime-independent; works on net9).</summary>
    Weaver,
}

/// <summary>How the app process is brought into the session.</summary>
public enum LaunchMode
{
    /// <summary>Force-stop, configure, relaunch (suspended until the session is up when <see cref="SessionSpec.SuspendOnStart"/>). Required for instrumenting (JIT-time).</summary>
    Restart,
    /// <summary>Attach to the running process: the app must already be configured to connect to the profiler port.</summary>
    Attach,
}

public enum SessionState { Idle, Preparing, WaitingForApp, Collecting, Analyzing, Ready, Failed }

/// <summary>Everything a session needs to run. Immutable; serialized to session.json.</summary>
public sealed record SessionSpec(
    string DeviceSerial,
    string Package,
    ProfilingMode Mode,
    LaunchMode Launch = LaunchMode.Restart,
    TimeSpan? Duration = null,
    bool SuspendOnStart = true,
    string? Callspec = null,
    bool TrackAllocations = true,
    string? Name = null,
    bool KeepAppRunning = false,
    InstrumentingEngine Engine = InstrumentingEngine.RuntimeProvider,
    IReadOnlyList<string>? WeaveAssemblies = null,
    IReadOnlyList<string>? WeaveReferenceDirs = null,
    string? WeaveMapPath = null,
    int SnapshotCount = 1,
    TimeSpan? SnapshotInterval = null,
    bool WeavePropertyAccessors = false,
    bool WeaveAsyncBodies = false);

/// <summary>Public snapshot of a session.</summary>
public sealed record SessionInfo(
    string Id,
    SessionState State,
    SessionSpec Spec,
    string Directory,
    string DatabasePath,
    string? TraceFile,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? EndedUtc,
    string? Error,
    IReadOnlyList<string> Warnings);

/// <summary>Thrown for session-level failures (prerequisites, state).</summary>
public sealed class ProfilerException : Exception
{
    public ProfilerException(string message) : base(message) { }
    public ProfilerException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The facade every frontend uses: orchestrates device setup, collection and
/// analysis for one profiling session and exposes the result database.
/// State machine: Idle -> Preparing -> WaitingForApp -> Collecting -> Analyzing -> Ready | Failed.
/// </summary>
public sealed class ProfilerSession : IAsyncDisposable
{
    public const string ToolVersion = "0.1.0";

    private readonly AdbClient _adb;
    private readonly List<string> _warnings = new();
    private readonly List<string> _log = new();
    private readonly CancellationTokenSource _stopRequested = new();
    private SessionState _state = SessionState.Idle;
    private string? _error;
    private DateTimeOffset? _started, _ended;
    private string? _traceFile;
    private DsRouterProcess? _dsrouter;
    private AppEnvironment? _env;
    private bool _reverseSet;
    private string? _expectedMarker;
    private bool _appLaunchedByUs;
    private WeaveDeployer? _weaveDeployer;
    private IReadOnlyList<WovenMethod>? _weaveMap;
    private string? _weaveEventsDir;
    private ResultStore? _store;

    private ProfilerSession(string id, SessionSpec spec, string directory, AdbClient adb)
    {
        Id = id; Spec = spec; Directory = directory; _adb = adb;
        CreatedUtc = DateTimeOffset.UtcNow;
    }

    public string Id { get; }
    public SessionSpec Spec { get; }
    public string Directory { get; }
    public DateTimeOffset CreatedUtc { get; }
    public string DatabasePath => Path.Combine(Directory, "session.db");
    public string LogPath => Path.Combine(Directory, "session.log");
    public SessionState State => _state;
    public event EventHandler<SessionInfo>? StateChanged;

    public SessionInfo Info => new(Id, _state, Spec, Directory, DatabasePath, _traceFile, CreatedUtc, _started, _ended, _error, _warnings.ToList());

    /// <summary>Create a new session directory under <paramref name="sessionsRoot"/>.</summary>
    public static ProfilerSession Create(SessionSpec spec, string sessionsRoot, AdbClient? adb = null)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string id = $"{stamp}-{Sanitize(spec.Name ?? spec.Package)}-{spec.Mode.ToString().ToLowerInvariant()}";
        string dir = Path.Combine(sessionsRoot, id);
        System.IO.Directory.CreateDirectory(dir);
        var s = new ProfilerSession(id, spec, dir, adb ?? new AdbClient());
        File.WriteAllText(Path.Combine(dir, "session.json"), JsonSerializer.Serialize(spec, JsonOpts));
        return s;
    }

    /// <summary>Open a finished session (its database) from disk.</summary>
    public static ResultStore OpenResults(string sessionDirectory) => ResultStore.Open(Path.Combine(sessionDirectory, "session.db"));

    /// <summary>Enumerate session directories under <paramref name="sessionsRoot"/> (newest first).</summary>
    public static IReadOnlyList<(string id, string directory, SessionSpec? spec, bool ready)> ListSessions(string sessionsRoot)
    {
        if (!System.IO.Directory.Exists(sessionsRoot)) return [];
        var list = new List<(string, string, SessionSpec?, bool)>();
        foreach (var dir in System.IO.Directory.GetDirectories(sessionsRoot).OrderByDescending(d => d))
        {
            SessionSpec? spec = null;
            try { spec = JsonSerializer.Deserialize<SessionSpec>(File.ReadAllText(Path.Combine(dir, "session.json")), JsonOpts); } catch { }
            list.Add((Path.GetFileName(dir), dir, spec, File.Exists(Path.Combine(dir, "session.db"))));
        }
        return list;
    }

    /// <summary>Result database (available once the session is Ready).</summary>
    public ResultStore Results => _store ?? throw new ProfilerException($"Session {Id} has no results yet (state {_state})");

    /// <summary>Run the whole session: prepare -> collect (Duration or until StopAsync) -> analyze.</summary>
    public async Task<SessionInfo> RunAsync(CancellationToken ct)
    {
        if (_state != SessionState.Idle) throw new ProfilerException($"Session {Id} already started ({_state})");
        try
        {
            await PrepareAsync(ct).ConfigureAwait(false);
            await CollectAsync(ct).ConfigureAwait(false);
            await AnalyzeAsync(ct).ConfigureAwait(false);
            SetState(SessionState.Ready);
        }
        catch (Exception e)
        {
            _error = e.Message;
            Log("FAILED: " + e);
            SetState(SessionState.Failed);
            await WriteFailedDbAsync().ConfigureAwait(false);
            if (e is ProfilerException or ToolException or OperationCanceledException) throw;
            throw new ProfilerException(e.Message, e);
        }
        finally
        {
            await CleanupAsync().ConfigureAwait(false);
        }
        return Info;
    }

    /// <summary>Request the end of collection for a session started without a duration.</summary>
    public void Stop() => _stopRequested.Cancel();

    // ------------------------------------------------------------ pipeline

    private async Task PrepareAsync(CancellationToken ct)
    {
        SetState(SessionState.Preparing);
        var devices = await _adb.ListDevicesAsync(ct).ConfigureAwait(false);
        var device = devices.FirstOrDefault(d => d.Serial == Spec.DeviceSerial)
            ?? throw new ProfilerException($"Device {Spec.DeviceSerial} is not attached (adb devices: {string.Join(", ", devices.Select(d => d.Serial))})");
        if (device.State != "device") throw new ProfilerException($"Device {Spec.DeviceSerial} is in state '{device.State}'");
        Log($"device {device.Serial} model={device.Model} api={device.ApiLevel} abi={device.Abi} emulator={device.IsEmulator}");

        var prereq = await new AppInspector(_adb).InspectAsync(device.Serial, Spec.Package, device.Abi, ct).ConfigureAwait(false);
        Log($"app debuggable={prereq.IsDebuggable} diagnostics={prereq.HasDiagnosticsComponent} aot={prereq.HasAotLibraries} monoDiagBaked={prereq.HasMonoDiagnosticsBaked}");
        var problems = prereq.Check(Spec.Mode);
        foreach (var p in problems.Where(p => !p.IsBlocking)) { _warnings.Add(p.Message); Log("warning: " + p.Message); }
        var blocking = problems.Where(p => p.IsBlocking).ToList();
        if (blocking.Count > 0)
            throw new ProfilerException("The app cannot be profiled in mode " + Spec.Mode + ":\n - " + string.Join("\n - ", blocking.Select(b => b.Message)));
        if (Spec.Mode == ProfilingMode.Instrumenting && Spec.Launch == LaunchMode.Attach)
            throw new ProfilerException("Instrumenting requires LaunchMode.Restart: the app must be (re)started under the instrumentation, not attached later.");

        if (Spec.Mode == ProfilingMode.Instrumenting && Spec.Engine == InstrumentingEngine.Weaver)
        {
            await PrepareWeaverAsync(device, prereq, ct).ConfigureAwait(false);
            return;
        }

        // Diagnostics transport.
        _dsrouter = await DsRouterProcess.StartAsync(device.IsEmulator, ct).ConfigureAwait(false);
        Log($"dsrouter pid={_dsrouter.Pid} appAddress={_dsrouter.AppAddress}");
        if (!device.IsEmulator)
        {
            await _adb.ReverseAsync(device.Serial, DsRouterProcess.AppPort, DsRouterProcess.DeviceHostPort, ct).ConfigureAwait(false);
            _reverseSet = true;
        }

        // App-side configuration.
        _env = new AppEnvironment(_adb, device.Serial, Spec.Package, device.Abi);
        if (Spec.Launch == LaunchMode.Restart)
        {
            await _adb.ForceStopAsync(device.Serial, Spec.Package, ct).ConfigureAwait(false);
            string ports = $"{_dsrouter.AppAddress},{(Spec.SuspendOnStart ? "suspend" : "nosuspend")},connect";
            if (prereq.IsDebuggable)
            {
                var updates = new List<KeyValuePair<string, string?>>
                {
                    new("DOTNET_DiagnosticPorts", ports),
                    new(EventPipeCollector.SessionMarkerVariable, Id),
                };
                if (Spec.Mode == ProfilingMode.Instrumenting)
                    updates.Add(new("MONO_DIAGNOSTICS", BuildMonoDiagnostics()));
                await _env.ApplyOverrideAsync(updates, ct).ConfigureAwait(false);
                _expectedMarker = Id;
                Log("override environment applied: " + string.Join(" ", updates.Select(u => u.Key + "=" + u.Value)));
            }
            else
            {
                await _env.SetDeviceProfilePropertyAsync(ports, ct).ConfigureAwait(false);
                Log("debug.mono.profile set: " + ports);
            }
            await _adb.LogcatClearAsync(device.Serial, ct).ConfigureAwait(false);
            await _adb.LaunchAsync(device.Serial, Spec.Package, ct).ConfigureAwait(false);
            _appLaunchedByUs = true;
            Log("app launched");
        }
        else
        {
            // Attach: the app's own DOTNET_DiagnosticPorts (default 127.0.0.1:9000,connect,nosuspend on EnableDiagnostics builds)
            // reaches dsrouter through adb reverse; emulators need the reverse too since 127.0.0.1 is the emulator itself.
            await _adb.ReverseAsync(device.Serial, DsRouterProcess.AppPort, device.IsEmulator ? DsRouterProcess.AppPort : DsRouterProcess.DeviceHostPort, ct).ConfigureAwait(false);
            _reverseSet = true;
            var pid = await _adb.PidOfAsync(device.Serial, Spec.Package, ct).ConfigureAwait(false)
                ?? throw new ProfilerException($"Attach requested but {Spec.Package} is not running on {device.Serial}");
            Log($"attaching to pid {pid}");
        }
    }

    private async Task PrepareWeaverAsync(DeviceInfo device, AppPrerequisites prereq, CancellationToken ct)
    {
        if (!prereq.IsDebuggable)
            throw new ProfilerException("Weaver instrumenting needs a debuggable build: the profiler configures the app and reads its results through run-as.");
        if (prereq.HasAotLibraries)
            _warnings.Add("The app contains AOT assemblies; only assemblies present as .dll in the override directory can be woven.");
        await _adb.ForceStopAsync(device.Serial, Spec.Package, ct).ConfigureAwait(false);
        _weaveDeployer = new WeaveDeployer(_adb, device.Serial, Spec.Package, device.Abi, Directory);

        if (!string.IsNullOrWhiteSpace(Spec.WeaveMapPath))
        {
            // The app was woven at build time (nap-weave + build/NetAndroidProfiler.Weaving.targets):
            // nothing to rewrite on the device, we only need the id map the build produced.
            if (!File.Exists(Spec.WeaveMapPath))
                throw new ProfilerException($"Weave map not found: {Spec.WeaveMapPath}. It is written by the build (default: <OutDir>nap-weave.map).");
            _weaveMap = CecilWeaver.ReadMap(Spec.WeaveMapPath!);
            if (_weaveMap.Count == 0)
                throw new ProfilerException($"Weave map {Spec.WeaveMapPath} is empty: the build wove no method.");
            Log($"using build-time weave map: {_weaveMap.Count} methods ({Spec.WeaveMapPath})");
        }
        else
        {
            var assemblies = Spec.WeaveAssemblies is { Count: > 0 } ? Spec.WeaveAssemblies : InferAssemblies();
            var filter = WeaveFilter.Parse(string.IsNullOrWhiteSpace(Spec.Callspec) ? "all" : Spec.Callspec!);
            Log($"weaving {string.Join(", ", assemblies)} with filter '{Spec.Callspec ?? "all"}'");
            _weaveMap = await _weaveDeployer.WeaveAndDeployAsync(assemblies, filter, null, ct, Spec.WeaveReferenceDirs, Spec.WeavePropertyAccessors, Spec.TrackAllocations, Spec.WeaveAsyncBodies).ConfigureAwait(false);
            Log($"woven {_weaveMap.Count} methods (skipped {_weaveDeployer.LastSkippedAccessorCount} property accessors); collector + assemblies deployed");
            if (_weaveDeployer.LastAsyncStubCount > 0)
                _warnings.Add($"{_weaveDeployer.LastAsyncStubCount} woven methods are async: the entry named after the method times its synchronous part up to the first await, " +
                              $"while '<method> (async body)' ({_weaveDeployer.LastAsyncBodyCount} of them) times what the method actually executed across its resumptions.");
        }

        // The collector writes to a private events dir; wipe stale files, then point the app at it.
        string eventsDir = _weaveDeployer.RemoteEventsDir;
        await _adb.RunAsAsync(device.Serial, Spec.Package, $"rm -rf {eventsDir} && mkdir -p {eventsDir}", ct).ConfigureAwait(false);
        string absoluteEvents = await _adb.RunAsAsync(device.Serial, Spec.Package, $"cd {eventsDir} && pwd", ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(Spec.WeaveMapPath))
        {
            // Build-time weaving: the app already carries NAP_PROFILER_OUT from its build.
            // Writing an override environment file here would create
            // files/.__override__/, which an app with embedded assemblies must not have.
            Log($"build-time weaving: environment comes from the app build (events in {absoluteEvents.Trim()})");
            await _weaveDeployer.ClearCollectorMarkerAsync(ct).ConfigureAwait(false);
            await _adb.LogcatClearAsync(device.Serial, ct).ConfigureAwait(false);
            await _adb.LaunchAsync(device.Serial, Spec.Package, ct).ConfigureAwait(false);
            _appLaunchedByUs = true;
            Log("app launched (weaver, build-time)");
            return;
        }

        _env = new AppEnvironment(_adb, device.Serial, Spec.Package, device.Abi);
        string eventsAbs = absoluteEvents.Trim();
        string markerDir = eventsAbs.Contains('/') ? eventsAbs[..eventsAbs.LastIndexOf('/')] : eventsAbs;
        await _env.ApplyOverrideAsync(
        [
            new("NAP_PROFILER_OUT", eventsAbs),
            new("NAP_PROFILER_MARKER_DIR", markerDir),
        ], ct).ConfigureAwait(false);
        Log($"NAP_PROFILER_OUT={eventsAbs} NAP_PROFILER_MARKER_DIR={markerDir}");
        await _weaveDeployer.ClearCollectorMarkerAsync(ct).ConfigureAwait(false);
        await _adb.LogcatClearAsync(device.Serial, ct).ConfigureAwait(false);
        await _adb.LaunchAsync(device.Serial, Spec.Package, ct).ConfigureAwait(false);
        _appLaunchedByUs = true;
        Log("app launched (weaver)");
    }

    private IReadOnlyList<string> InferAssemblies()
    {
        // Assembly name = the leading dotted segments of the callspec targets, best effort:
        // N:App.Droid -> App.Droid, T:App.Core.X.Y -> App.Core. Falls back to the package's last segment.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (Spec.Callspec ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string s = entry.TrimStart('+', '-');
            if (s.Length > 2 && s[1] == ':') s = s[2..];
            var parts = s.Split('.');
            if (parts.Length >= 2) names.Add(parts[0] + "." + parts[1]);
            else if (parts.Length == 1 && parts[0].Length > 0) names.Add(parts[0]);
        }
        if (names.Count == 0) throw new ProfilerException("Cannot infer which assemblies to weave from the callspec; pass WeaveAssemblies explicitly.");
        return names.ToList();
    }

    private async Task CollectWeaverAsync(CancellationToken ct)
    {
        SetState(SessionState.WaitingForApp);
        // The collector writes a marker the first time a woven method executes. No
        // marker means either that the app is not running the woven assemblies at all
        // (it loads them from inside the APK) or that the weave scope is so wide that
        // startup has not reached managed code yet.
        var markerWait = TimeSpan.FromSeconds(240);
        if (!await _weaveDeployer!.WaitForCollectorMarkerAsync(markerWait, ct).ConfigureAwait(false))
            throw new ProfilerException(
                $"No woven method of {Spec.Package} executed within {markerWait.TotalSeconds:F0} s " +
                $"({_weaveMap!.Count} methods woven). Two common causes: " +
                "(1) the app loads its assemblies from inside the APK (EmbedAssembliesIntoApk=true), so the woven copies " +
                "in the fast-deployment directory are ignored - rebuild with -p:EmbedAssembliesIntoApk=false and reinstall; " +
                "(2) the weave filter is too wide - instrumenting thousands of methods makes startup far slower than this " +
                "timeout (measured on the reference application: ~7900 methods had not reached managed code after 2 minutes, while a single " +
                "type starts normally), so narrow the callspec to the types you are investigating. " +
                "Apps that must keep their assemblies embedded can be woven at build time instead " +
                "(build/NetAndroidProfiler.Weaving.targets + WeaveMapPath). See docs/APP_SETUP.md.");
        Log("collector marker seen: woven code is running");
        SetState(SessionState.Collecting);
        _started = DateTimeOffset.UtcNow;
        var duration = Spec.Duration ?? TimeSpan.FromSeconds(20);
        using (var waitCts = CancellationTokenSource.CreateLinkedTokenSource(_stopRequested.Token, ct))
        {
            waitCts.CancelAfter(duration);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, waitCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        ct.ThrowIfCancellationRequested();
        // Force-stop triggers the collector's ProcessExit flush; then pull the event files.
        await _adb.ForceStopAsync(Spec.DeviceSerial, Spec.Package, ct).ConfigureAwait(false);
        _appLaunchedByUs = false; // already stopped
        await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        _weaveEventsDir = Path.Combine(Directory, "events");
        int n = await _weaveDeployer!.PullEventsAsync(_weaveDeployer.RemoteEventsDir, _weaveEventsDir, ct).ConfigureAwait(false);
        Log($"pulled {n} event files");
        _ended = DateTimeOffset.UtcNow;
    }

    private async Task CollectAsync(CancellationToken ct)
    {
        if (Spec.Mode == ProfilingMode.Instrumenting && Spec.Engine == InstrumentingEngine.Weaver)
        {
            await CollectWeaverAsync(ct).ConfigureAwait(false);
            return;
        }
        SetState(SessionState.WaitingForApp);
        var collector = new EventPipeCollector(_dsrouter!.Pid, Log);
        await collector.WaitForRuntimeAsync(TimeSpan.FromSeconds(Spec.Launch == LaunchMode.Attach ? 20 : 90), ct, _expectedMarker).ConfigureAwait(false);
        SetState(SessionState.Collecting);
        _started = DateTimeOffset.UtcNow;

        switch (Spec.Mode)
        {
            case ProfilingMode.Sampling:
            case ProfilingMode.Instrumenting:
            {
                _traceFile = Path.Combine(Directory, "trace.nettrace");
                var providers = Spec.Mode == ProfilingMode.Sampling ? ProviderSets.Sampling() : ProviderSets.Instrumenting(Spec.TrackAllocations);
                await collector.CollectToFileAsync(providers, _traceFile, Spec.Duration, _stopRequested.Token, ct).ConfigureAwait(false);
                break;
            }
            case ProfilingMode.HeapSnapshot:
            {
                if (Spec.Launch == LaunchMode.Restart)
                {
                    // A heap dump requested while the runtime is still initializing yields nothing:
                    // let the app warm up (Duration doubles as the warm-up time for heap sessions).
                    var warmUp = Spec.Duration ?? TimeSpan.FromSeconds(5);
                    Log($"heap snapshot: warming up {warmUp.TotalSeconds:F0}s after launch");
                    await Task.Delay(warmUp, ct).ConfigureAwait(false);
                }
                int count = Math.Max(1, Spec.SnapshotCount);
                var interval = Spec.SnapshotInterval ?? TimeSpan.FromSeconds(30);
                for (int i = 0; i < count; i++)
                {
                    if (i > 0)
                    {
                        Log($"waiting {interval.TotalSeconds:F0}s before snapshot {i + 1}/{count}");
                        await Task.Delay(interval, ct).ConfigureAwait(false);
                    }
                    var snap = await collector.TakeHeapSnapshotAsync(TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
                    _heapSnapshots.Add(snap);
                    Log($"snapshot {i + 1}/{count}: {snap.TotalObjects} objects, {snap.TotalBytes} bytes");
                }
                break;
            }
        }
        _ended = DateTimeOffset.UtcNow;
    }

    private readonly List<HeapSnapshot> _heapSnapshots = new();

    private async Task AnalyzeAsync(CancellationToken ct)
    {
        SetState(SessionState.Analyzing);
        if (File.Exists(DatabasePath)) File.Delete(DatabasePath);
        var store = ResultStore.Create(DatabasePath, ToolVersion);
        long? total = null, withStack = null;
        switch (Spec.Mode)
        {
            case ProfilingMode.Sampling:
            {
                var r = await Task.Run(() => new SamplingAnalyzer().Analyze(_traceFile!, ct), ct).ConfigureAwait(false);
                store.WriteSampling(r);
                total = r.TotalSamples; withStack = r.SamplesWithStack;
                Log($"sampling analyzed: samples={r.TotalSamples} withStack={r.SamplesWithStack} methods={r.Methods.Count}");
                break;
            }
            case ProfilingMode.Instrumenting:
            {
                InstrumentingResult r = Spec.Engine == InstrumentingEngine.Weaver
                    ? await Task.Run(() => new WeaveAnalyzer().Analyze(_weaveEventsDir!, _weaveMap!, ct), ct).ConfigureAwait(false)
                    : await Task.Run(() => new MonoProfilerAnalyzer().Analyze(_traceFile!, ct), ct).ConfigureAwait(false);
                store.WriteInstrumenting(r);
                Log($"instrumenting analyzed ({Spec.Engine}): enter={r.EnterEvents} leave={r.LeaveEvents} allocs={r.AllocationEvents} methods={r.Methods.Count}");
                if (r.EnterEvents == 0) _warnings.Add("No enter/leave events were recorded: check the callspec/assemblies and that the app actually ran the woven methods.");
                break;
            }
            case ProfilingMode.HeapSnapshot:
            {
                foreach (var snap in _heapSnapshots)
                {
                    int id = store.WriteHeapSnapshot(snap.TakenUtc, null, snap.ByType);
                    Log($"heap snapshot {id}: objects={snap.TotalObjects} bytes={snap.TotalBytes} types={snap.ByType.Count}");
                }
                break;
            }
        }
        store.WriteSession(new SessionRow(Id, Spec.Mode.ToString(), "Ready", Spec.Package, Spec.DeviceSerial, _started,
            _started is not null && _ended is not null ? (_ended.Value - _started.Value).TotalMilliseconds : null,
            _traceFile is null ? null : Path.GetFileName(_traceFile), total, withStack, JsonSerializer.Serialize(Spec, JsonOpts), null));
        store.Dispose();
        _store = ResultStore.Open(DatabasePath);
    }

    private async Task WriteFailedDbAsync()
    {
        try
        {
            if (File.Exists(DatabasePath)) return;
            using var store = ResultStore.Create(DatabasePath, ToolVersion);
            store.WriteSession(new SessionRow(Id, Spec.Mode.ToString(), "Failed", Spec.Package, Spec.DeviceSerial, _started, null, null, null, null, JsonSerializer.Serialize(Spec, JsonOpts), _error));
        }
        catch (Exception e) { Log("cannot write failed-session db: " + e.Message); }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task CleanupAsync()
    {
        var ct = CancellationToken.None;
        // An app we launched keeps the injected DOTNET_DiagnosticPorts in its process environment and would
        // reconnect to the next session's dsrouter (and be profiled instead of the intended app): stop it.
        if (_appLaunchedByUs && !Spec.KeepAppRunning)
        {
            try { await _adb.ForceStopAsync(Spec.DeviceSerial, Spec.Package, ct).ConfigureAwait(false); Log("app stopped (KeepAppRunning=false)"); }
            catch (Exception e) { Log("force-stop failed: " + e.Message); }
            _appLaunchedByUs = false;
        }
        try { if (_weaveDeployer is not null && _weaveDeployer.HasPendingChanges) { await _weaveDeployer.RestoreAsync(ct).ConfigureAwait(false); Log("woven assemblies restored"); } }
        catch (Exception e) { Log("restore woven assemblies failed: " + e.Message); }
        try { if (_env is not null && _env.HasPendingChanges) { await _env.RestoreAsync(ct).ConfigureAwait(false); Log("app environment restored"); } }
        catch (Exception e) { Log("restore environment failed: " + e.Message); }
        try { if (_reverseSet) await _adb.ReverseRemoveAsync(Spec.DeviceSerial, DsRouterProcess.AppPort, ct).ConfigureAwait(false); }
        catch (Exception e) { Log("adb reverse --remove failed: " + e.Message); }
        if (_dsrouter is not null) { await _dsrouter.DisposeAsync().ConfigureAwait(false); _dsrouter = null; }
        try { await File.WriteAllLinesAsync(LogPath, _log, ct).ConfigureAwait(false); } catch { }
    }

    private string BuildMonoDiagnostics()
    {
        var parts = new List<string> { "--diagnostic-mono-profiler=enable" };
        if (Spec.TrackAllocations) parts.Add("--diagnostic-mono-profiler=alloc");
        string callspec = string.IsNullOrWhiteSpace(Spec.Callspec) ? "all" : Spec.Callspec.Trim();
        parts.Add("--diagnostic-mono-profiler-callspec=" + callspec);
        return string.Join(' ', parts);
    }

    private void SetState(SessionState s)
    {
        _state = s;
        Log("state " + s);
        StateChanged?.Invoke(this, Info);
    }

    private void Log(string line)
    {
        lock (_log) _log.Add($"{DateTime.Now:HH:mm:ss.fff} {line}");
    }

    /// <summary>Session log lines (also written to session.log at the end).</summary>
    public IReadOnlyList<string> LogLines { get { lock (_log) return _log.ToList(); } }

    public async ValueTask DisposeAsync()
    {
        _store?.Dispose();
        await CleanupAsync().ConfigureAwait(false);
        _stopRequested.Dispose();
    }

    private static string Sanitize(string s) => new(s.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
}
