using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using NetAndroidProfiler.Core.Apps;
using NetAndroidProfiler.Core.Devices;
using NetAndroidProfiler.Core.Projects;
using NetAndroidProfiler.Core.Sessions;

namespace NetAndroidProfiler.Cli;

/// <summary>
/// The local control service the Delphi GUI drives (KNOWN_UNKNOWNS U7): HTTP + JSON on
/// loopback, one session registry per process.
///
/// Results deliberately do not travel over this channel - the GUI opens session.db
/// itself, which is what lets it analyze sessions produced by any frontend. What goes
/// over the wire is only what a database cannot answer: which devices exist, whether an
/// app can be profiled, and the live state of a running session.
/// </summary>
public sealed class ControlService : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly SessionRegistry _registry;
    private readonly JobRegistry _jobs = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;

    public ControlService(int port, string? sessionsRoot = null)
    {
        Port = port > 0 ? port : FreePort();
        _registry = new SessionRegistry(sessionsRoot, Log);
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
    }

    public int Port { get; }
    public string SessionsRoot => _registry.SessionsRoot;

    /// <summary>Raised when a client asks the service to shut down (POST /shutdown).</summary>
    public CancellationToken Stopping => _stopping.Token;

    /// <summary>
    /// The one place that knows how the wire looks. Source-generated rather than
    /// reflection-based so that the service also runs in a Native AOT build, where
    /// reflection-based System.Text.Json throws at the first call.
    /// </summary>
    private static JsonSerializerContext Json => ControlJsonContext.Default;

    /// <summary>HttpListener has no "any free port", so take one from the OS and hand it over.</summary>
    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    private void Log(string line) => Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (HttpListenerException) { return; }          // listener stopped
            catch (ObjectDisposedException) { return; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var (status, body) = await RouteAsync(ctx).ConfigureAwait(false);
            await WriteAsync(ctx, status, body).ConfigureAwait(false);
        }
        catch (ProfilerException e) { await WriteAsync(ctx, 400, new ErrorResponse(e.Message)).ConfigureAwait(false); }
        catch (ToolException e) { await WriteAsync(ctx, 400, new ErrorResponse(e.Message)).ConfigureAwait(false); }
        catch (Exception e)
        {
            Log("request failed: " + e);
            try { await WriteAsync(ctx, 500, new ErrorResponse(e.Message)).ConfigureAwait(false); } catch { }
        }
    }

    private async Task<(int status, object body)> RouteAsync(HttpListenerContext ctx)
    {
        string method = ctx.Request.HttpMethod;
        var segments = (ctx.Request.Url?.AbsolutePath ?? "/").Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        var query = ctx.Request.QueryString;
        var ct = _stopping.Token;

        switch (segments)
        {
            case []:
            case ["health"] when method == "GET":
                return (200, new HealthResponse(ProfilerSession.ToolVersion, SessionsRoot, Port));

            case ["shutdown"] when method == "POST":
                _stopping.Cancel();
                return (200, new OkResponse("shutting down"));

            case ["devices"] when method == "GET":
            {
                var devices = await new AdbClient().ListDevicesAsync(ct).ConfigureAwait(false);
                return (200, devices);
            }

            case ["apps", var package, "check"] when method == "GET":
            {
                string serial = Required(query["device"], "device");
                var adb = new AdbClient();
                var device = (await adb.ListDevicesAsync(ct).ConfigureAwait(false)).FirstOrDefault(d => d.Serial == serial)
                    ?? throw new ProfilerException($"Device {serial} is not attached.");
                var prereq = await new AppInspector(adb).InspectAsync(serial, package, device.Abi, ct).ConfigureAwait(false);
                var mode = SessionSpecFactory.ParseMode(query["mode"] ?? "sampling");
                return (200, new CheckResponse(prereq, prereq.Check(mode)));
            }

            // ---------------------------------------------------------- the machine

            case ["prereqs"] when method == "GET":
                return (200, new PrereqResponse(ToolLocator.Prerequisites()));

            case ["prereqs", "install"] when method == "POST":
            {
                var request = await ReadBodyAsync<InstallToolRequest>(ctx).ConfigureAwait(false)
                    ?? throw new ProfilerException("Which tool to install is required.");
                string tool = Required(request.Tool, "tool");
                // Refuse an unknown tool here rather than inside the job: a caller that got
                // the name wrong should see a 400, not a job that fails a second later.
                var known = ToolLocator.Prerequisites()
                    .FirstOrDefault(t => t.Name.Equals(tool, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ProfilerException($"'{tool}' is not one of the tools this profiler installs.");
                if (known.InstallCommand is null)
                    throw new ProfilerException($"{known.Name} cannot be installed automatically. {known.Fix}");
                var job = _jobs.Start("install", (log, token) => ToolLocator.InstallAsync(known.Name, log, token));
                return (201, Describe(job, 0));
            }

            // ---------------------------------------------------------- the sources

            case ["projects"] when method == "GET":
                return (200, AppProjectFinder
                    .Find(Required(query["path"], "path"), query["configuration"] ?? "Debug")
                    .ToList());

            case ["projects", "candidates"] when method == "GET":
            {
                string outputDir = Required(query["outputDir"], "outputDir");
                var assemblies = (query["assemblies"] ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (assemblies.Length == 0) throw new ProfilerException("Query parameter 'assemblies' is required.");
                return (200, AppProjectFinder.Candidates(outputDir, assemblies).ToList());
            }

            case ["builds"] when method == "POST":
            {
                var request = await ReadBodyAsync<BuildRequest>(ctx).ConfigureAwait(false)
                    ?? throw new ProfilerException("A build request body is required.");
                var build = request.ToRequest();
                AppBuilder.ArgumentsFor(build);          // fail now, with a 400, not inside the job
                var job = _jobs.Start("build", (log, token) => AppBuilder.RunAsync(build, log, token));
                return (201, Describe(job, 0));
            }

            case ["jobs", var id] when method == "GET":
                return (200, Describe(JobOrThrow(id), int.TryParse(query["from"], out int from) ? from : 0));

            case ["jobs", var id, "cancel"] when method == "POST":
            {
                var job = JobOrThrow(id);
                job.Cancel();
                return (200, Describe(job, 0));
            }

            case ["sessions"] when method == "GET":
                return (200, ProfilerSession.ListSessions(SessionsRoot)
                    .Select(s => new SessionListItem(s.id, s.ready))
                    .ToList());

            case ["sessions"] when method == "POST":
            {
                var request = await ReadBodyAsync<StartRequest>(ctx).ConfigureAwait(false)
                    ?? throw new ProfilerException("A session request body is required.");
                var spec = request.ToSpec();
                var live = _registry.Create(spec);
                live.RunTask = Task.Run(() => live.Session.RunAsync(CancellationToken.None));
                return (201, Describe(live));
            }

            case ["sessions", var id] when method == "GET":
                return (200, Describe(LiveOrThrow(id)));

            case ["sessions", var id, "counters"] when method == "GET":
                return (200, LiveOrThrow(id).Session.Counters());

            case ["sessions", var id, "stop"] when method == "POST":
            {
                var live = LiveOrThrow(id);
                live.Session.Stop();
                return (200, Describe(live));
            }

            // The live-control verbs of U7. Core refuses them on the runtime-provider
            // engine with an explanation, which travels back as a 400.
            case ["sessions", var id, "pause"] when method == "POST":
            {
                var live = LiveOrThrow(id);
                await live.Session.PauseAsync(ct).ConfigureAwait(false);
                return (200, Describe(live));
            }

            case ["sessions", var id, "resume"] when method == "POST":
            {
                var live = LiveOrThrow(id);
                await live.Session.ResumeAsync(ct).ConfigureAwait(false);
                return (200, Describe(live));
            }

            case ["sessions", var id, "snapshot"] when method == "POST":
            {
                var live = LiveOrThrow(id);
                int segment = await live.Session.SnapshotAsync(ct).ConfigureAwait(false);
                return (200, new SnapshotResponse(segment, live.Session.DatabasePath, Describe(live)));
            }

            // Keeping a Get Results: the copy is taken by the session that owns the
            // database, but listing them is a question about a directory, which is why an
            // old session answers it too.
            case ["sessions", var id, "archive"] when method == "POST":
            {
                var request = await ReadBodyAsync<ArchiveRequest>(ctx).ConfigureAwait(false);
                var live = LiveOrThrow(id);
                return (201, await live.Session.ArchiveAsync(request?.Name, ct).ConfigureAwait(false));
            }

            case ["sessions", var id, "archives"] when method == "GET":
            {
                string directory = Path.Combine(SessionsRoot, id);
                if (!System.IO.Directory.Exists(directory))
                    throw new ProfilerException($"No session '{id}' under {SessionsRoot}.");
                return (200, ProfilerSession.ListArchives(directory).ToList());
            }

            case ["sessions", var id, "clear"] when method == "POST":
            {
                var live = LiveOrThrow(id);
                await live.Session.ClearAsync(ct).ConfigureAwait(false);
                return (200, Describe(live));
            }

            default:
                return (404, new ErrorResponse($"No route for {method} {ctx.Request.Url?.AbsolutePath}."));
        }
    }

    private JobRegistry.Job JobOrThrow(string id) =>
        _jobs.Find(id) ?? throw new ProfilerException($"Job '{id}' was not started by this service.");

    private static JobResponse Describe(JobRegistry.Job job, int from)
    {
        var (first, total, lines) = job.LogFrom(from);
        return new JobResponse(job.Id, job.Kind, job.State, job.ExitCode, job.Error, first, total, lines);
    }

    private SessionRegistry.LiveSession LiveOrThrow(string id) =>
        _registry.Live(id) ?? throw new ProfilerException($"Session '{id}' was not started by this service.");

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ProfilerException($"Query parameter '{name}' is required.") : value;

    private static SessionResponse Describe(SessionRegistry.LiveSession live)
    {
        var info = live.Session.Info;
        return new SessionResponse(
            info.Id,
            info.State.ToString(),
            info.Directory,
            info.DatabasePath,
            info.Error,
            info.Warnings,
            live.Session.LogLines.TakeLast(20).ToList());
    }

    private static async Task<T?> ReadBodyAsync<T>(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        string body = await reader.ReadToEndAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body)) return default;
        var typeInfo = (JsonTypeInfo<T>?)Json.GetTypeInfo(typeof(T))
            ?? throw new ProfilerException($"{typeof(T).Name} is not part of the control service contract.");
        try { return JsonSerializer.Deserialize(body, typeInfo); }
        catch (JsonException e) { throw new ProfilerException("Malformed JSON body: " + e.Message); }
    }

    private static async Task WriteAsync(HttpListenerContext ctx, int status, object body)
    {
        var typeInfo = Json.GetTypeInfo(body.GetType())
            ?? throw new InvalidOperationException(
                $"{body.GetType().Name} is returned by the control service but not registered in ControlJsonContext.");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body, typeInfo);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        _jobs.Dispose();
        if (_loop is not null) { try { await _loop.ConfigureAwait(false); } catch { } }
        await _registry.DisposeAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }
}

public sealed record HealthResponse(string Version, string SessionsRoot, int Port);
public sealed record OkResponse(string Message);
public sealed record ErrorResponse(string Error);
public sealed record SessionListItem(string Id, bool Ready);
public sealed record SnapshotResponse(int Segment, string DatabasePath, SessionResponse Session);
public sealed record CheckResponse(AppPrerequisites App, IReadOnlyList<PrerequisiteProblem> Problems);
/// <summary>The external tools this machine offers, so a frontend can say what is missing before a session fails.</summary>
public sealed record PrereqResponse(IReadOnlyList<ToolStatus> Tools);
/// <summary>A background job (a build, a tool install) with the slice of its log the caller asked for.</summary>
public sealed record JobResponse(
    string Id, string Kind, string State, int? ExitCode, string? Error,
    int LogFrom, int LogTotal, IReadOnlyList<string> Log);
public sealed record SessionResponse(
    string Id, string State, string Directory, string DatabasePath,
    string? Error, IReadOnlyList<string> Warnings, IReadOnlyList<string> Log);

/// <summary>Body of POST /sessions: the loose shape a frontend sends, translated by Core.</summary>
public sealed class StartRequest
{
    public string DeviceSerial { get; set; } = "";
    public string PackageName { get; set; } = "";
    public string Mode { get; set; } = "sampling";
    public string Launch { get; set; } = "restart";
    public int? DurationSeconds { get; set; }
    public string? Callspec { get; set; }
    public bool TrackAllocations { get; set; } = true;
    public bool SuspendOnStart { get; set; } = true;
    public string? Name { get; set; }
    public bool KeepAppRunning { get; set; }
    public string Engine { get; set; } = "auto";
    public List<string>? WeaveAssemblies { get; set; }
    public List<string>? WeaveReferenceDirs { get; set; }
    public string? WeaveMapPath { get; set; }
    public int Snapshots { get; set; } = 1;
    public int SnapshotIntervalSeconds { get; set; } = 30;
    public bool WeavePropertyAccessors { get; set; }
    public bool WeaveAsyncBodies { get; set; } = true;
    public int MaxTraceMb { get; set; } = 512;
    /// <summary>Build output with the app's portable .pdb files; lets the GUI show the source.</summary>
    public string? SymbolsDir { get; set; }

    public SessionSpec ToSpec() => SessionSpecFactory.Build(
        DeviceSerial, PackageName, Mode, Launch, DurationSeconds, Callspec, TrackAllocations, SuspendOnStart,
        Name, KeepAppRunning, Engine, WeaveAssemblies, WeaveReferenceDirs, WeaveMapPath,
        Snapshots, SnapshotIntervalSeconds, WeavePropertyAccessors, WeaveAsyncBodies, MaxTraceMb, SymbolsDir);
}

/// <summary>Body of POST /sessions/{id}/archive: what to call the results being kept.</summary>
public sealed class ArchiveRequest
{
    public string? Name { get; set; }
}

/// <summary>Body of POST /prereqs/install: which of the known tools to install.</summary>
public sealed class InstallToolRequest
{
    public string Tool { get; set; } = "";
}

/// <summary>
/// Body of POST /builds: build and install an app project with the properties a
/// profiling session needs. The profiler never rebuilds on its own - this runs only when
/// a frontend asks.
/// </summary>
public sealed class BuildRequest
{
    public string ProjectPath { get; set; } = "";
    public string Configuration { get; set; } = "Debug";
    public string? DeviceSerial { get; set; }
    public bool EnableDiagnostics { get; set; } = true;
    public bool FastDeployment { get; set; } = true;
    public bool Install { get; set; } = true;
    /// <summary>Clear the app's fast-deployment directory first: for an app changing deployment mode.</summary>
    public bool ClearDeployedAssemblies { get; set; }
    /// <summary>The app's package, needed only to clear its deployed assemblies.</summary>
    public string? PackageName { get; set; }
    /// <summary>Weave the app while it is built: for an app that keeps its assemblies inside the APK.</summary>
    public bool Weave { get; set; }
    /// <summary>Which methods that weave covers; required with <see cref="Weave"/>.</summary>
    public string? Callspec { get; set; }
    /// <summary>Referenced assemblies to weave as well as the app own assembly, by name.</summary>
    public List<string>? WeaveAssemblies { get; set; }

    public AppBuildRequest ToRequest() =>
        new(ProjectPath, Configuration, DeviceSerial, EnableDiagnostics, FastDeployment, Install,
            ClearDeployedAssemblies, PackageName, Weave, Callspec, null, WeaveAssemblies);
}

/// <summary>
/// Everything the control service puts on the wire, in one place. A response type that is
/// missing here fails loudly at the first request rather than silently in a published
/// build: see WriteAsync.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(OkResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(SessionListItem))]
[JsonSerializable(typeof(List<SessionListItem>))]
[JsonSerializable(typeof(SnapshotResponse))]
[JsonSerializable(typeof(CheckResponse))]
[JsonSerializable(typeof(SessionResponse))]
[JsonSerializable(typeof(StartRequest))]
[JsonSerializable(typeof(SessionCounters))]
[JsonSerializable(typeof(PrereqResponse))]
[JsonSerializable(typeof(InstallToolRequest))]
[JsonSerializable(typeof(BuildRequest))]
[JsonSerializable(typeof(JobResponse))]
[JsonSerializable(typeof(AppProjectInfo))]
[JsonSerializable(typeof(List<AppProjectInfo>))]
[JsonSerializable(typeof(ArchiveRequest))]
[JsonSerializable(typeof(ArchivedResult))]
[JsonSerializable(typeof(List<ArchivedResult>))]
[JsonSerializable(typeof(CallspecCandidate))]
[JsonSerializable(typeof(List<CallspecCandidate>))]
[JsonSerializable(typeof(IReadOnlyList<DeviceInfo>))]
[JsonSerializable(typeof(List<DeviceInfo>))]
internal sealed partial class ControlJsonContext : JsonSerializerContext;
