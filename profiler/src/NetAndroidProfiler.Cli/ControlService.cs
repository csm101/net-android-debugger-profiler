using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetAndroidProfiler.Core.Apps;
using NetAndroidProfiler.Core.Devices;
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

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

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

            case ["sessions", var id, "stop"] when method == "POST":
            {
                var live = LiveOrThrow(id);
                live.Session.Stop();
                return (200, Describe(live));
            }

            // The live-control verbs of U7. They are part of the contract on purpose:
            // the GUI is written against the final shape, and the engine work behind them
            // (collector toggle, session segments) lands without changing the wire.
            case ["sessions", var id, "pause" or "resume" or "snapshot" or "clear"] when method == "POST":
            {
                _ = LiveOrThrow(id);
                string verb = segments[2];
                return (501, new ErrorResponse(
                    $"'{verb}' is part of the control contract but is not implemented yet: it needs session segments in " +
                    "Core (and, for a real pause, the collector's Enabled flag on the device). Use stop for now."));
            }

            default:
                return (404, new ErrorResponse($"No route for {method} {ctx.Request.Url?.AbsolutePath}."));
        }
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
        try { return JsonSerializer.Deserialize<T>(body, Json); }
        catch (JsonException e) { throw new ProfilerException("Malformed JSON body: " + e.Message); }
    }

    private static async Task WriteAsync(HttpListenerContext ctx, int status, object body)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), Json);
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
        if (_loop is not null) { try { await _loop.ConfigureAwait(false); } catch { } }
        await _registry.DisposeAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }
}

public sealed record HealthResponse(string Version, string SessionsRoot, int Port);
public sealed record OkResponse(string Message);
public sealed record ErrorResponse(string Error);
public sealed record SessionListItem(string Id, bool Ready);
public sealed record CheckResponse(AppPrerequisites App, IReadOnlyList<PrerequisiteProblem> Problems);
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
    public string Engine { get; set; } = "provider";
    public List<string>? WeaveAssemblies { get; set; }
    public List<string>? WeaveReferenceDirs { get; set; }
    public string? WeaveMapPath { get; set; }
    public int Snapshots { get; set; } = 1;
    public int SnapshotIntervalSeconds { get; set; } = 30;
    public bool WeavePropertyAccessors { get; set; }
    public bool WeaveAsyncBodies { get; set; } = true;
    public int MaxTraceMb { get; set; } = 512;

    public SessionSpec ToSpec() => SessionSpecFactory.Build(
        DeviceSerial, PackageName, Mode, Launch, DurationSeconds, Callspec, TrackAllocations, SuspendOnStart,
        Name, KeepAppRunning, Engine, WeaveAssemblies, WeaveReferenceDirs, WeaveMapPath,
        Snapshots, SnapshotIntervalSeconds, WeavePropertyAccessors, WeaveAsyncBodies, MaxTraceMb);
}
