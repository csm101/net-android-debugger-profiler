using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    /// <summary>
    /// Mono.Cecil IL weaving of the app assemblies (runtime-independent; works on net9).
    /// The woven code writes one record per enter and per leave, so the order of calls and
    /// every single duration survive - and the cost grows with the number of calls.
    /// </summary>
    Weaver,
    /// <summary>
    /// The same weaving, with the app keeping a calling context tree instead of writing
    /// events: a call updates counters on the node for its path. The call tree, the graph,
    /// parents and children and the critical path all survive; the order of calls and
    /// individual durations beyond min/max do not. This is what makes instrumenting a whole
    /// application affordable, and it is how AQTime has always worked.
    /// </summary>
    WeaverTree,
    /// <summary>
    /// Let the session choose once it has looked at the app: the weaver when its assemblies
    /// can be rewritten on the device, the runtime provider when they cannot. This is the
    /// default because the right answer depends on the app, not on the caller's taste.
    /// </summary>
    Auto,
}

/// <summary>Which engines rewrite the app's IL: both weaver modes do, the provider does not.</summary>
public static class InstrumentingEngines
{
    public static bool Weaves(this InstrumentingEngine engine) =>
        engine is InstrumentingEngine.Weaver or InstrumentingEngine.WeaverTree;
}

/// <summary>How the app process is brought into the session.</summary>
public enum LaunchMode
{
    /// <summary>Force-stop, configure, relaunch (suspended until the session is up when <see cref="SessionSpec.SuspendOnStart"/>). Required for instrumenting (JIT-time).</summary>
    Restart,
    /// <summary>Attach to the running process: the app must already be configured to connect to the profiler port.</summary>
    Attach,
}

public enum SessionState { Idle, Preparing, WaitingForApp, WaitingToRecord, Collecting, Analyzing, Ready, Failed }

/// <summary>
/// Source-generated serialization for the session spec. Reflection-based System.Text.Json
/// cannot run in a Native AOT build - it throws at the first call - and this type is
/// written to session.json and to the database on every session. It lives at namespace
/// level because the generator does not emit for a type nested in a non-partial class.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SessionSpec))]
[JsonSerializable(typeof(ArchivedResult))]
internal sealed partial class SessionJsonContext : JsonSerializerContext;

/// <summary>
/// Results kept under a name while the session went on: an ordinary result database plus
/// what a frontend needs to list it. See <see cref="ProfilerSession.ArchiveAsync"/>.
/// </summary>
/// <param name="Name">What the user called it.</param>
/// <param name="Path">The archived database, openable like any session database.</param>
/// <param name="CreatedUtc">When it was taken.</param>
/// <param name="Segment">The refresh it holds, when it was taken from a running session.</param>
public sealed record ArchivedResult(string Name, string Path, DateTimeOffset CreatedUtc, int? Segment);

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
    bool WeaveAsyncBodies = true,
    long? MaxTraceBytes = SessionSpec.DefaultMaxTraceBytes,
    /// <summary>
    /// Build output holding the app's portable .pdb files. When given, the analysis records
    /// where each method lives, which is what lets a frontend show the source next to the
    /// figures. The profiler is used by whoever built the app, so asking is legitimate.
    /// </summary>
    string? SymbolsDir = null,
    /// <summary>
    /// The project the app was built from, and the solution that holds it, when the
    /// frontend knows them. Nothing in a session needs them: they are recorded so that a
    /// list of sessions can be read as "what I profiled of this product", which is how
    /// anyone with more than one app looks for yesterday's run. Sessions written before
    /// this existed simply have neither.
    /// </summary>
    string? ProjectPath = null,
    /// <inheritdoc cref="ProjectPath"/>
    string? SolutionPath = null,
    /// <summary>
    /// Set the session up, let the app run, and record nothing until somebody says "now"
    /// (AQTime's "start with profiling disabled"). What needs measuring is rarely the
    /// application's startup: it is what happens when a person presses a certain button,
    /// and everything recorded before they get there is noise to be waded through. With
    /// this the app is never suspended at launch, whatever <see cref="SuspendOnStart"/>
    /// says - waiting for a diagnostic session is exactly what is being deferred.
    /// </summary>
    bool StartPaused = false)
{
    /// <summary>
    /// Default ceiling for a .nettrace: a session left running fills the disk otherwise
    /// (a real app samples at ~1.5 MB/s, so this is roughly six minutes of sampling and
    /// far less of instrumenting). Pass null for no limit.
    /// </summary>
    public const long DefaultMaxTraceBytes = 512L * 1024 * 1024;
}

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

/// <summary>
/// What a running session can report cheaply, once a second, without disturbing it:
/// enough for a live monitor, and nothing that needs the device.
/// </summary>
/// <param name="State">Current state.</param>
/// <param name="ElapsedSeconds">Since collection started, 0 before that.</param>
/// <param name="TraceBytes">Size of the trace being written (provider engines).</param>
/// <param name="EventBytes">Size of the event files pulled so far (weaver engine).</param>
/// <param name="Snapshots">How many times the results have been refreshed.</param>
public sealed record SessionCounters(string State, double ElapsedSeconds, long TraceBytes, long EventBytes, int Snapshots);

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
    /// <summary>
    /// Version stamped into every session database and reported by /health and the MCP
    /// server. It comes from the assembly, which comes from Directory.Build.props: one
    /// edit per release, and no way for the stamp to drift from what shipped.
    /// </summary>
    public static readonly string ToolVersion =
        typeof(ProfilerSession).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    private readonly AdbClient _adb;
    private readonly List<string> _warnings = new();
    private readonly List<string> _log = new();
    private readonly CancellationTokenSource _stopRequested = new();
    private SessionState _state = SessionState.Idle;
    private string? _error;
    private DateTimeOffset? _started, _ended;
    private string? _traceFile;
    private DsRouterProcess? _dsrouter;
    private ResultStore? _writeStore;
    private AppEnvironment? _env;
    private bool _reverseSet;
    private string? _expectedMarker;
    private bool _appLaunchedByUs;
    private WeaveDeployer? _weaveDeployer;
    private IReadOnlyList<WovenMethod>? _weaveMap;
    private string? _weaveEventsDir;
    private ResultStore? _store;
    /// <summary>Completed when somebody asks a paused session to start recording.</summary>
    private readonly TaskCompletionSource _recordRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>The session was stopped while it was still waiting to record.</summary>
    private bool _recordedNothing;
    /// <summary>
    /// The build's pdbs, read once and kept for the life of the session: every snapshot
    /// resolves source locations, and a real app's symbols take seconds to read.
    /// </summary>
    private Symbols.PortablePdbSymbols? _pdbs;
    private bool _pdbsTried;

    private ProfilerSession(string id, SessionSpec spec, string directory, AdbClient adb)
    {
        Id = id; Spec = spec; Directory = directory; _adb = adb;
        CreatedUtc = DateTimeOffset.UtcNow;
    }

    public string Id { get; }
    public SessionSpec Spec { get; private set; }
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
        File.WriteAllText(Path.Combine(dir, "session.json"), JsonSerializer.Serialize(spec, SessionJsonContext.Default.SessionSpec));
        return s;
    }

    /// <summary>Open a finished session (its database) from disk.</summary>
    public static ResultStore OpenResults(string sessionDirectory) => ResultStore.Open(Path.Combine(sessionDirectory, "session.db"));

    /// <summary>
    /// Give a session on disk a name, or take its name away (null). The name is what a
    /// frontend lists it by; the directory keeps the id it was created with, because the
    /// id is what everything else - archives, logs, an open database - refers to.
    /// </summary>
    public static void Rename(string sessionDirectory, string? name)
    {
        string specPath = Path.Combine(sessionDirectory, "session.json");
        if (!File.Exists(specPath))
            throw new ProfilerException($"No session.json in '{sessionDirectory}': this session cannot be renamed.");
        var spec = JsonSerializer.Deserialize(File.ReadAllText(specPath), SessionJsonContext.Default.SessionSpec)
            ?? throw new ProfilerException($"The session spec in '{sessionDirectory}' could not be read.");
        string? trimmed = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        File.WriteAllText(specPath, JsonSerializer.Serialize(spec with { Name = trimmed }, SessionJsonContext.Default.SessionSpec));
    }

    /// <summary>
    /// Delete a session directory and everything in it: the database, the trace, the log
    /// and the archives kept during it. Deliberately not recoverable - a profiling session
    /// is a recording, and a frontend that offers this asks first.
    /// </summary>
    public static void Delete(string sessionDirectory)
    {
        if (!System.IO.Directory.Exists(sessionDirectory))
            throw new ProfilerException($"No session directory at '{sessionDirectory}'.");
        System.IO.Directory.Delete(sessionDirectory, recursive: true);
    }

    /// <summary>Enumerate session directories under <paramref name="sessionsRoot"/> (newest first).</summary>
    public static IReadOnlyList<(string id, string directory, SessionSpec? spec, bool ready)> ListSessions(string sessionsRoot)
    {
        if (!System.IO.Directory.Exists(sessionsRoot)) return [];
        var list = new List<(string, string, SessionSpec?, bool)>();
        foreach (var dir in System.IO.Directory.GetDirectories(sessionsRoot).OrderByDescending(d => d))
        {
            SessionSpec? spec = null;
            try { spec = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(dir, "session.json")), SessionJsonContext.Default.SessionSpec); } catch { }
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
            // Stopped before it ever started recording: there is no trace to analyze, and
            // saying so is better than failing a session that did exactly what was asked.
            if (_recordedNothing)
            {
                _warnings.Add("Stopped before recording started: nothing was collected, so this session has no results.");
                Log("stopped while waiting to record: no results");
                SetState(SessionState.Ready);
                return Info;
            }
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

    /// <summary>
    /// A cheap snapshot of how the session is going, for a monitor that polls: file sizes
    /// and elapsed time, never a device round trip.
    /// </summary>
    public SessionCounters Counters()
    {
        long traceBytes = 0;
        if (_traceFile is not null)
        {
            try { traceBytes = new FileInfo(_traceFile).Length; } catch { }
        }
        long eventBytes = 0;
        if (_weaveEventsDir is not null && System.IO.Directory.Exists(_weaveEventsDir))
        {
            try
            {
                foreach (string f in System.IO.Directory.GetFiles(_weaveEventsDir, "*.napw"))
                    eventBytes += new FileInfo(f).Length;
            }
            catch { }
        }
        double elapsed = _started is null ? 0 : ((_ended ?? DateTimeOffset.UtcNow) - _started.Value).TotalSeconds;
        return new SessionCounters(_state.ToString(), elapsed, traceBytes, eventBytes, _snapshotCount);
    }

    private int _snapshotCount;

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

        if (Spec.Mode == ProfilingMode.Instrumenting && Spec.Engine == InstrumentingEngine.Auto)
        {
            // The weaver needs to replace the app's assemblies in the fast-deployment
            // directory: possible for a debuggable app that does not carry them inside the
            // APK. When it is possible it is the better engine - it does not depend on the
            // runtime instrumenting anything, it works on net9 (U20), and it survives a
            // device that has stopped instrumenting (U23).
            Spec = Spec with { Engine = ChooseEngine(prereq.IsDebuggable, prereq.HasAssemblyStore, Spec.WeaveMapPath) };
            Log("engine: " + (Spec.Engine == InstrumentingEngine.WeaverTree
                ? string.IsNullOrWhiteSpace(Spec.WeaveMapPath)
                    ? "weaver-tree (the app is fast-deployed, so its assemblies can be woven)"
                    : "weaver-tree (the build already wove the app: nothing has to be rewritten on the device)"
                : "provider (the app carries its assemblies inside the APK, so nothing can be rewritten on the device)"));
        }

        if (Spec.Mode == ProfilingMode.Instrumenting && Spec.Engine.Weaves())
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
            // Suspending holds the runtime at startup until a session resumes it. Sampling
            // and instrumenting do resume it - that is how they catch app init - but a heap
            // dump never does: the app would sit frozen through the warm-up and the dump
            // would find an empty heap. Measured: with suspend the session ends with "no
            // objects" however long the warm-up is; without it, the same command works.
            bool suspend = Spec.SuspendOnStart && Spec.Mode != ProfilingMode.HeapSnapshot;
            if (Spec.SuspendOnStart && !suspend)
                Log("heap snapshot: launching without suspend (a suspended app allocates nothing)");
            string ports = $"{_dsrouter.AppAddress},{(suspend ? "suspend" : "nosuspend")},connect";
            // The per-app override environment file is only safe for fast-deployed apps:
            // creating files/.__override__/ for an app that carries its assemblies inside
            // the APK makes the runtime look for them there and the app stops starting.
            if (prereq.IsDebuggable && !prereq.HasAssemblyStore)
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
                if (DevicePropertyOverride.ForeignValueWarning(DeviceGlobals.DebugMonoProfile,
                        await _env.ProfileProperty.ReadAsync(ct).ConfigureAwait(false), 0, deadlineRequired: false) is { } inUse)
                    Log(inUse);
                await _env.SetDeviceProfilePropertyAsync(ports, ct).ConfigureAwait(false);
                Log($"debug.mono.profile set: {ports}" + (prereq.HasAssemblyStore ? " (app embeds its assemblies: no per-app environment file)" : ""));
                if (Spec.Mode == ProfilingMode.Instrumenting && Spec.Engine == InstrumentingEngine.RuntimeProvider)
                    throw new ProfilerException(
                        $"{Spec.Package} carries its assemblies inside the APK, so MONO_DIAGNOSTICS cannot be injected per session " +
                        "(writing the app's override environment file would stop it from starting). Bake MONO_DIAGNOSTICS into the " +
                        "build (docs/APP_SETUP.md), rebuild with -p:EmbedAssembliesIntoApk=false, or use engine=weaver with a " +
                        "build-time weave map.");
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
            await EnsureAttachableAsync(device, prereq, ct).ConfigureAwait(false);
            Log($"attaching to pid {pid}");
        }
    }

    /// <summary>
    /// Which engine <see cref="InstrumentingEngine.Auto"/> means for this app. The weaver needs
    /// the app's assemblies to be replaceable in its fast-deployment directory - or already woven
    /// by its build, in which case nothing has to be rewritten on the device at all and an app
    /// that embeds its assemblies is weavable too.
    /// </summary>
    public static InstrumentingEngine ChooseEngine(bool isDebuggable, bool hasAssemblyStore, string? weaveMapPath)
    {
        if (!string.IsNullOrWhiteSpace(weaveMapPath)) return InstrumentingEngine.WeaverTree;
        return isDebuggable && !hasAssemblyStore ? InstrumentingEngine.WeaverTree : InstrumentingEngine.RuntimeProvider;
    }

    /// <summary>
    /// Attach profiles a process that is already running, so nothing we do now can give it a
    /// diagnostics port: it had to be started with one. When it was not, the session would
    /// otherwise wait for a runtime that is never going to connect and fail with an obscure
    /// transport error, so check the three places a port can come from and say what to do.
    /// </summary>
    private async Task EnsureAttachableAsync(DeviceInfo device, AppPrerequisites prereq, CancellationToken ct)
    {
        if (prereq.BakedEnvironmentHints.Any(h => h.Contains("DOTNET_DiagnosticPorts", StringComparison.Ordinal))) return;
        string prop = await _adb.GetPropAsync(device.Serial, DeviceGlobals.DebugMonoProfile, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(prop)) return;
        if (prereq.IsDebuggable && !prereq.HasAssemblyStore)
        {
            var current = await _env!.ReadOverrideAsync(ct).ConfigureAwait(false);
            if (current.Any(kv => kv.Key == "DOTNET_DiagnosticPorts")) return;
        }
        throw new ProfilerException(
            $"{Spec.Package} is running, but nothing configured a diagnostics port for it, so it will never connect to the profiler. " +
            "Attach only works for an app started with DOTNET_DiagnosticPorts (baked into the build, in the app's override environment, " +
            "or through the device property debug.mono.profile - see docs/APP_SETUP.md). Use Launch=Restart to have the profiler " +
            "configure and start the app itself.");
    }

    private async Task PrepareWeaverAsync(DeviceInfo device, AppPrerequisites prereq, CancellationToken ct)
    {
        if (!prereq.IsDebuggable)
            throw new ProfilerException("Weaver instrumenting needs a debuggable build: the profiler configures the app and reads its results through run-as.");
        if (prereq.HasAssemblyStore && string.IsNullOrWhiteSpace(Spec.WeaveMapPath))
            throw new ProfilerException(
                $"{Spec.Package} carries its assemblies inside the APK (assembly store), so rewriting the fast-deployment copies " +
                "would have no effect - and writing into the app's override directory stops such an app from starting. " +
                "Weave it during its own build instead (-p:NapWeave=true with build/NetAndroidProfiler.Weaving.targets) and pass " +
                "the resulting nap-weave.map, or rebuild the app with -p:EmbedAssembliesIntoApk=false. See docs/APP_SETUP.md.");
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
            // The app records what its build baked into it. A session that asked for the
            // other kind would wait for files nobody writes, so it follows the app and says
            // so - the alternative is a session that never shows anything and never
            // explains itself.
            string bakedMode = CecilWeaver.ReadMapMode(Spec.WeaveMapPath!);
            var bakedEngine = bakedMode.Equals("tree", StringComparison.OrdinalIgnoreCase)
                ? InstrumentingEngine.WeaverTree
                : InstrumentingEngine.Weaver;
            if (Spec.Engine != bakedEngine)
            {
                if (Spec.Engine != InstrumentingEngine.Auto)
                    _warnings.Add($"The app was woven during its build to record as '{bakedMode}', which is baked into it: " +
                                  $"this session follows that instead of {Spec.Engine.ToString().ToLowerInvariant()}. " +
                                  "Rebuild with -p:NapMode=" + (bakedEngine == InstrumentingEngine.WeaverTree ? "trace" : "tree") +
                                  " to change it.");
                Spec = Spec with { Engine = bakedEngine };
                Log($"build-time weave records as '{bakedMode}': engine set to {bakedEngine}");
            }
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
            new("NAP_PROFILER_MODE", Spec.Engine == InstrumentingEngine.WeaverTree ? "tree" : "trace"),
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

    /// <summary>
    /// Where a session started paused waits: everything is in place, the app is running,
    /// and nothing is measured until <see cref="StartRecordingAsync"/>. False means the
    /// session was stopped while waiting, so there is nothing to collect or analyze.
    /// </summary>
    private async Task<bool> WaitForTheWordAsync(CancellationToken ct)
    {
        if (!Spec.StartPaused) return true;
        SetState(SessionState.WaitingToRecord);
        Log("waiting for the word to record: the app is running and nothing is being measured");
        var stopped = new TaskCompletionSource();
        using (_stopRequested.Token.Register(() => stopped.TrySetResult()))
        using (ct.Register(() => stopped.TrySetResult()))
            await Task.WhenAny(_recordRequested.Task, stopped.Task).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (_recordRequested.Task.IsCompletedSuccessfully) return true;
        _recordedNothing = true;
        _ended = DateTimeOffset.UtcNow;
        return false;
    }

    /// <summary>
    /// Begin recording in a session started with <see cref="SessionSpec.StartPaused"/>.
    /// The app has been running all along: this is the moment the measuring starts.
    /// </summary>
    public Task StartRecordingAsync(CancellationToken ct = default)
    {
        if (!Spec.StartPaused)
            throw new ProfilerException($"Session {Id} was not started paused: it has been recording since it began.");
        if (_state != SessionState.WaitingToRecord)
            throw new ProfilerException($"Session {Id} is {_state}, not waiting to record.");
        _recordRequested.TrySetResult();
        Log("recording started on request");
        return Task.CompletedTask;
    }

    private async Task CollectWeaverAsync(CancellationToken ct)
    {
        // The app keeps the control file of whatever session touched it last, so this session's
        // generation starts from that number instead of from zero: otherwise its first clear
        // rewrites a generation the collector is already on and nothing is cleared.
        await _weaveDeployer!.AdoptDeviceGenerationAsync(ct).ConfigureAwait(false);
        // Paused first, so that the woven methods executed while somebody navigates to the
        // screen worth measuring are not recorded. They still run woven: the overhead is
        // there, only the recording is not.
        if (Spec.StartPaused)
        {
            await _weaveDeployer!.SetCollectingAsync(false, ct).ConfigureAwait(false);
            if (!await WaitForTheWordAsync(ct).ConfigureAwait(false)) return;
            await _weaveDeployer!.SetCollectingAsync(true, ct).ConfigureAwait(false);
        }
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
        using (var waitCts = CancellationTokenSource.CreateLinkedTokenSource(_stopRequested.Token, ct))
        {
            // No duration means "until someone stops it": that is what a GUI session is, and
            // what makes snapshot and pause useful. Only a stated duration ends it by itself.
            if (Spec.Duration is { } duration) waitCts.CancelAfter(duration);
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
        if (Spec.Mode == ProfilingMode.Instrumenting && Spec.Engine.Weaves())
        {
            await CollectWeaverAsync(ct).ConfigureAwait(false);
            return;
        }
        // Nothing is connected to the runtime until this returns: an EventPipe session
        // records from the moment it opens, so a session that must not measure the startup
        // simply does not open one yet.
        if (!await WaitForTheWordAsync(ct).ConfigureAwait(false)) return;
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
                var collected = await collector.CollectToFileAsync(providers, _traceFile, Spec.Duration, _stopRequested.Token, ct, maxBytes: Spec.MaxTraceBytes).ConfigureAwait(false);
                if (collected.StoppedBySizeLimit)
                {
                    string warning = $"Collection ended early: the trace reached its {collected.Bytes / (1024 * 1024)} MB limit " +
                                     "and the results cover only the part collected before that. Raise MaxTraceBytes, shorten the " +
                                     "session, or narrow the callspec (instrumenting traces grow fastest).";
                    _warnings.Add(warning);
                    Log("warning: " + warning);
                }
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
        // A snapshot taken during the run already created the database: reuse it so its
        // segment history survives, and let the write path replace the result tables.
        var store = TakeWriteStore();
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
                InstrumentingResult r = Spec.Engine.Weaves()
                    ? await AnalyzeWeaverEventsAsync(ct).ConfigureAwait(false)
                    : await Task.Run(() => new MonoProfilerAnalyzer().Analyze(_traceFile!, ct), ct).ConfigureAwait(false);
                store.WriteInstrumenting(r);
                Log($"instrumenting analyzed ({Spec.Engine}): enter={r.EnterEvents} leave={r.LeaveEvents} allocs={r.AllocationEvents} methods={r.Methods.Count}" +
                    (r.BrokenPairs > 0 ? $" brokenPairs={r.BrokenPairs}" : ""));
                if (r.BrokenPairs > 0)
                    _warnings.Add($"{r.BrokenPairs} enter/leave pairs were dropped because their records were cut " +
                                  "(results cleared while the app was running, or a truncated read). The remaining timings are consistent.");
                if (r.EnterEvents == 0 && r.AllocationEvents > 0 && Spec.Engine == InstrumentingEngine.RuntimeProvider)
                    _warnings.Add(
                        "Allocations were recorded but no enter/leave: the Mono profiler stops instrumenting methods on a device " +
                        "that has been profiled for a while, and keeps emitting allocations, so the timings look empty rather " +
                        "than missing. Restart the device or emulator and retry - measured to restore it - or use engine=weaver, " +
                        "which does not depend on the runtime's instrumentation.");
                else if (r.EnterEvents == 0)
                    _warnings.Add("No enter/leave events were recorded: check the callspec/assemblies and that the app actually ran the woven methods.");
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
            _traceFile is null ? null : Path.GetFileName(_traceFile), total, withStack, JsonSerializer.Serialize(Spec, SessionJsonContext.Default.SessionSpec), null));
        WriteMethodSources(store);
        store.AddSegment(DateTimeOffset.UtcNow, "final", total ?? 0);
        store.Dispose();
        _store = ResultStore.Open(DatabasePath);
    }

    /// <summary>
    /// Record where each method lives, from the portable pdbs of the build output. Failing
    /// to read the symbols is a warning, never a failed session: the profile is still valid,
    /// it just cannot be shown next to the source.
    /// </summary>
    private void WriteMethodSources(ResultStore store)
    {
        // Said out loud rather than passed over in silence: without symbols the results
        // can never be shown next to the source, and by the time anyone notices, the build
        // those pdbs belong to may be gone.
        if (string.IsNullOrWhiteSpace(Spec.SymbolsDir))
        {
            _warnings.Add("No symbols directory: this session records no source locations. "
                + "Pass symbolsDir (the app's bin/<Configuration>/<tfm>) or projectPath when starting one.");
            return;
        }
        try
        {
            if (!System.IO.Directory.Exists(Spec.SymbolsDir))
            {
                _warnings.Add($"Symbols directory not found: {Spec.SymbolsDir}");
                return;
            }
            if (!_pdbsTried)
            {
                _pdbsTried = true;
                _pdbs = Symbols.PortablePdbSymbols.LoadDirectory(Spec.SymbolsDir);
            }
            var pdbs = _pdbs;
            if (pdbs is null || pdbs.Modules.Count == 0)
            {
                _warnings.Add($"No portable pdb files in {Spec.SymbolsDir} (DebugType must be portable).");
                return;
            }
            var found = new List<(int, string, int, int)>();
            foreach (var m in store.Methods())
            {
                if (m.Token == 0 || string.IsNullOrEmpty(m.Module)) continue;
                var range = pdbs.Find(m.Module, m.Token);
                if (range is not null) found.Add((m.Id, range.Document, range.StartLine, range.EndLine));
            }
            store.WriteMethodSources(found);
            Log($"source locations resolved for {found.Count} methods");
        }
        catch (Exception e)
        {
            _warnings.Add($"Could not read the symbols in {Spec.SymbolsDir}: {e.Message}");
        }
    }

    /// <summary>Let go of the symbols; the session is over or being disposed.</summary>
    private void ReleaseSymbols()
    {
        _pdbs?.Dispose();
        _pdbs = null;
    }

    /// <summary>The writable store of this session, created on first use (a snapshot or the analysis).</summary>
    private ResultStore TakeWriteStore()
    {
        var store = _writeStore ?? (File.Exists(DatabasePath)
            ? ResultStore.Open(DatabasePath, readOnly: false)
            : ResultStore.Create(DatabasePath, ToolVersion));
        _writeStore = null;                     // ownership moves to the caller
        return store;
    }

    // ------------------------------------------------------- live control (U7)

    /// <summary>
    /// The weaver's events, or an empty result when there are none. A session cleared and
    /// then stopped before the app produced anything again has nothing to analyze, and an
    /// empty result is the truth: failing the session would throw away the run instead.
    /// </summary>
    private async Task<InstrumentingResult> AnalyzeWeaverEventsAsync(CancellationToken ct)
    {
        try
        {
            return await Task.Run(() => AnalyzeWeaverOutput(ct), ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            _warnings.Add("No events were collected: the session recorded nothing after the last clear, " +
                          "or no woven method ran. The results are empty, not lost.");
            Log("instrumenting: no event files to analyze");
            return new InstrumentingResult(_started ?? DateTimeOffset.UtcNow, TimeSpan.Zero,
                [], [], [], [], [], [], [], 0, 0, 0, 0);
        }
    }

    /// <summary>
    /// Whichever of the two the app produced: nodes when it kept a call tree, events when it
    /// wrote a stream. The directory decides rather than the spec, so a session that was
    /// switched, or an app left running from an earlier one, is read as what it actually
    /// wrote.
    /// </summary>
    private InstrumentingResult AnalyzeWeaverOutput(CancellationToken ct) =>
        WeaveTreeAnalyzer.HasTrees(_weaveEventsDir!)
            ? new WeaveTreeAnalyzer().Analyze(_weaveEventsDir!, _weaveMap!, ct)
            : new WeaveAnalyzer().Analyze(_weaveEventsDir!, _weaveMap!, ct);

    /// <summary>
    /// Refresh the results from what has been collected so far, without stopping the app -
    /// AQTime's "Get Results". Only the weaver engine can do this: its events are files the
    /// collector flushes every second and its method names come from the weave map, while
    /// the runtime provider only emits the names that resolve a trace when its session ends.
    /// </summary>
    public async Task<int> SnapshotAsync(CancellationToken ct = default)
    {
        RequireLiveWeaverSession("snapshot");
        _weaveEventsDir ??= Path.Combine(Directory, "events");
        int files = await _weaveDeployer!.PullEventsAsync(_weaveDeployer.RemoteEventsDir, _weaveEventsDir, ct).ConfigureAwait(false);
        InstrumentingResult result;
        try
        {
            result = await Task.Run(() => AnalyzeWeaverOutput(ct), ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            // Right after a clear there is nothing yet: an empty snapshot is the truth, not
            // a failure. The results stay empty until the app produces events again.
            Log("snapshot: no events collected yet");
            _writeStore ??= File.Exists(DatabasePath) ? ResultStore.Open(DatabasePath, readOnly: false) : ResultStore.Create(DatabasePath, ToolVersion);
            return _writeStore.AddSegment(DateTimeOffset.UtcNow, "snapshot", 0, "no events yet");
        }

        _writeStore ??= File.Exists(DatabasePath) ? ResultStore.Open(DatabasePath, readOnly: false) : ResultStore.Create(DatabasePath, ToolVersion);
        _writeStore.WriteInstrumenting(result);
        // The methods are new to the database, so their source locations are too. Doing this
        // only at the end of the session was the reason a running session showed "no source
        // location" for an app whose symbols it had all along - and a running session is
        // exactly when somebody wants to read the code next to the figures.
        WriteMethodSources(_writeStore);
        _writeStore.WriteSession(new SessionRow(Id, Spec.Mode.ToString(), _state.ToString(), Spec.Package, Spec.DeviceSerial,
            _started, null, null, null, null, JsonSerializer.Serialize(Spec, SessionJsonContext.Default.SessionSpec), null));
        int segment = _writeStore.AddSegment(DateTimeOffset.UtcNow, "snapshot", result.EnterEvents, $"{files} event files");
        _snapshotCount++;
        Log($"snapshot {segment}: enter={result.EnterEvents} leave={result.LeaveEvents} methods={result.Methods.Count} (session continues)");
        return segment;
    }

    /// <summary>
    /// Keeps the results as they are now, under a name, and goes on profiling: AQTime's
    /// habit of collecting a Get Results and never losing it.
    /// <para>
    /// A snapshot rewrites the session's result tables, so the way to keep one is to copy
    /// the database - which is also the cheapest: an archive is an ordinary result
    /// database, it needs no schema of its own, it opens in any frontend without a live
    /// session, and nothing is kept that was not asked for. On a live weaver session this
    /// refreshes the results first, so the archive holds what the app has done up to this
    /// moment; on a session that has ended it simply keeps a copy of the final results.
    /// </para>
    /// </summary>
    /// <param name="name">Display name; a default is used when it is empty.</param>
    public async Task<ArchivedResult> ArchiveAsync(string? name, CancellationToken ct = default)
    {
        int? segment = null;
        if (CanControlLive) segment = await SnapshotAsync(ct).ConfigureAwait(false);

        var store = _writeStore;
        bool borrowed = store is null;
        if (store is null)
        {
            if (!File.Exists(DatabasePath))
                throw new ProfilerException($"Session {Id} has no results to archive yet (state {_state}).");
            store = ResultStore.Open(DatabasePath, readOnly: true);
        }
        try
        {
            var created = DateTimeOffset.UtcNow;
            string display = string.IsNullOrWhiteSpace(name)
                ? $"snapshot {_snapshotCount} - {created.ToLocalTime():HH:mm:ss}"
                : name.Trim();
            string path = FreeArchivePath(Directory, display);
            store.BackupTo(path);
            var archive = new ArchivedResult(display, path, created, segment);
            File.WriteAllText(Path.ChangeExtension(path, ".json"),
                JsonSerializer.Serialize(archive, SessionJsonContext.Default.ArchivedResult));
            Log($"archived '{display}' to {path} (the session continues)");
            return archive;
        }
        finally
        {
            if (borrowed) store.Dispose();
        }
    }

    /// <summary>The archived results of a session directory, newest first. No session need be running.</summary>
    public static IReadOnlyList<ArchivedResult> ListArchives(string sessionDirectory)
    {
        string folder = Path.Combine(sessionDirectory, ArchivesFolder);
        if (!System.IO.Directory.Exists(folder)) return [];
        var list = new List<ArchivedResult>();
        foreach (string db in System.IO.Directory.GetFiles(folder, "*.db"))
        {
            ArchivedResult? described = null;
            try
            {
                string sidecar = Path.ChangeExtension(db, ".json");
                if (File.Exists(sidecar))
                    described = JsonSerializer.Deserialize(File.ReadAllText(sidecar), SessionJsonContext.Default.ArchivedResult);
            }
            catch (Exception) { /* a lost or broken sidecar only costs the pretty name */ }
            // The file is the archive; the sidecar only decorates it. One without the other
            // is still perfectly openable, so it is listed either way.
            list.Add(described is null
                ? new ArchivedResult(Path.GetFileNameWithoutExtension(db), db, File.GetCreationTimeUtc(db), null)
                : described with { Path = db });
        }
        return list.OrderByDescending(a => a.CreatedUtc).ToList();
    }

    private const string ArchivesFolder = "archives";

    /// <summary>A file name for the display name that no other archive is using.</summary>
    private static string FreeArchivePath(string sessionDirectory, string display)
    {
        var slug = new string(display.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray()).Trim();
        if (slug.Length == 0) slug = "archive";
        if (slug.Length > 80) slug = slug[..80];
        string folder = Path.Combine(sessionDirectory, ArchivesFolder);
        System.IO.Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, slug + ".db");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{slug} ({i}).db");
        return path;
    }

    /// <summary>Stop recording events without stopping the app (AQTime's Disable Profiling).</summary>
    public async Task PauseAsync(CancellationToken ct = default)
    {
        RequireLiveWeaverSession("pause");
        await _weaveDeployer!.SetCollectingAsync(false, ct).ConfigureAwait(false);
        Log("collection paused (the woven methods stay woven, so their overhead remains)");
    }

    /// <summary>Resume recording after <see cref="PauseAsync"/>.</summary>
    public async Task ResumeAsync(CancellationToken ct = default)
    {
        // A session waiting for the word has not started yet: resuming it is starting it,
        // whatever engine it runs on.
        if (_state == SessionState.WaitingToRecord)
        {
            await StartRecordingAsync(ct).ConfigureAwait(false);
            return;
        }
        RequireLiveWeaverSession("resume");
        await _weaveDeployer!.SetCollectingAsync(true, ct).ConfigureAwait(false);
        Log("collection resumed");
    }

    /// <summary>Throw away what was collected so far and keep going (AQTime's Clear Results).</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        RequireLiveWeaverSession("clear");
        await _weaveDeployer!.ClearEventsAsync(ct).ConfigureAwait(false);
        if (_weaveEventsDir is not null)
            WeaveDeployer.DeletePulledEvents(_weaveEventsDir);
        if (File.Exists(DatabasePath))
        {
            _writeStore ??= ResultStore.Open(DatabasePath, readOnly: false);
            _writeStore.ClearResults();
            _writeStore.AddSegment(DateTimeOffset.UtcNow, "clear", 0);
        }
        Log("results cleared (the app keeps running and stays instrumented)");
    }

    /// <summary>
    /// Whether the live verbs (snapshot, pause, resume, clear) can serve this session:
    /// it is collecting, and its engine writes files rather than one trace that only
    /// resolves at the end. <see cref="RequireLiveWeaverSession"/> is the same question
    /// asked when the answer has to be an explanation.
    /// </summary>
    public bool CanControlLive =>
        _state is (SessionState.Collecting or SessionState.WaitingForApp)
        && Spec.Mode == ProfilingMode.Instrumenting && Spec.Engine.Weaves()
        && _weaveDeployer is not null && _weaveMap is not null;

    private void RequireLiveWeaverSession(string operation)
    {
        if (_state is not (SessionState.Collecting or SessionState.WaitingForApp))
            throw new ProfilerException($"Cannot {operation}: the session is {_state}, not collecting.");
        if (Spec.Mode != ProfilingMode.Instrumenting || !Spec.Engine.Weaves() || _weaveDeployer is null || _weaveMap is null)
            throw new ProfilerException(
                $"'{operation}' needs the weaver engine (mode=instrumenting, engine=weaver): its events are files the collector " +
                "flushes as it goes. A runtime-provider trace only becomes readable when its session ends, so there stop and " +
                "start another session instead.");
    }

    private async Task WriteFailedDbAsync()
    {
        try
        {
            // A snapshot may already have created it while the session was running.
            using var store = File.Exists(DatabasePath)
                ? ResultStore.Open(DatabasePath, readOnly: false)
                : ResultStore.Create(DatabasePath, ToolVersion);
            store.WriteSession(new SessionRow(Id, Spec.Mode.ToString(), "Failed", Spec.Package, Spec.DeviceSerial, _started, null, null, null, null, JsonSerializer.Serialize(Spec, SessionJsonContext.Default.SessionSpec), _error));
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
        if (_dsrouter is not null)
        {
            // What the router saw is the only account of the device side of a failed session.
            if (_state != SessionState.Ready)
                foreach (var line in _dsrouter.Log.Where(l => !l.TrimEnd().EndsWith("[0]", StringComparison.Ordinal)).TakeLast(40))
                    Log("dsrouter| " + line.Trim());
            await _dsrouter.DisposeAsync().ConfigureAwait(false);
            _dsrouter = null;
        }
        if (_writeStore is not null) { try { _writeStore.Dispose(); } catch { } _writeStore = null; }
        try { await File.WriteAllLinesAsync(LogPath, _log, ct).ConfigureAwait(false); } catch { }
    }

    /// <summary>
    /// MONO_DIAGNOSTICS for the runtime-provider engine. Note that asking for allocations
    /// here costs the enter/leave events entirely - see the warning raised after analysis
    /// and ANDROID_PROFILING_NOTES; the option is still honoured because an
    /// allocations-only provider session is a legitimate thing to want.
    /// </summary>
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
        ReleaseSymbols();
        await CleanupAsync().ConfigureAwait(false);
        _stopRequested.Dispose();
    }

    private static string Sanitize(string s) => new(s.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());

}
