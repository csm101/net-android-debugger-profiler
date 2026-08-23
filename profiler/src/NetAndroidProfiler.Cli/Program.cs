// nap: the non-MCP entry point to the profiler Core.
//
//   nap serve [--port <n>] [--sessions-root <dir>]
//       Local control service for the Delphi GUI (KNOWN_UNKNOWNS U7). HTTP + JSON on
//       127.0.0.1. With no --port (or --port 0) a free one is taken and printed as a
//       JSON line on stdout, so the caller that spawned the process can read it.
//
//   nap devices
//       Attached devices as JSON.
//
//   nap run --package <id> [--device <serial>] [--mode sampling|instrumenting|heap] ...
//       One session, start to finish, for scripts and CI: profiles, analyses, and
//       prints where the result database is. Everything the GUI and the MCP server
//       can start, minus the interaction.
//
//   nap doctor
//       What the profiler found on this machine: adb, dotnet-dsrouter, and whether
//       they come from this package or from the system.
//
//   nap version
using System.Text.Json;
using System.Text.Json.Serialization;
using NetAndroidProfiler.Cli;
using NetAndroidProfiler.Core.Devices;
using NetAndroidProfiler.Core.Sessions;


string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
switch (command)
{
    case "serve":
        return await ServeAsync(Option(args, "--port") is { } p ? int.Parse(p) : 0, Option(args, "--sessions-root"));

    case "devices":
    {
        var devices = await new AdbClient().ListDevicesAsync(CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(devices, NapJsonContext.Default.IReadOnlyListDeviceInfo));
        return 0;
    }

    case "run":
        return await RunAsync(args);

    case "doctor":
        return Doctor();

    case "version":
        Console.WriteLine(ProfilerSession.ToolVersion);
        return 0;

    default:
        Console.Error.WriteLine("""
            nap - .NET for Android profiler

              nap serve [--port <n>] [--sessions-root <dir>]   local control service (HTTP/JSON on 127.0.0.1)
              nap devices                                      attached devices as JSON
              nap run --package <id> [options]                 profile once and analyse
              nap doctor                                       report the tools this machine offers
              nap version

            nap run options:
              --device <serial>        default: the only attached device
              --mode <m>               sampling (default) | instrumenting | heap
              --duration <seconds>     default: 20 (heap: as long as the snapshots need)
              --launch <l>             restart (default) | attach
              --engine <e>             instrumenting only: auto (default) | weaver-tree | weaver | provider
                                       auto weaves when the app allows it; weaver-tree keeps a call
                                       tree in the app, weaver writes an event per call
              --callspec <spec>        instrumenting only, required: N:Ns, T:Type, M:Type:Method
              --no-allocations         instrumenting only: time methods without recording allocations
              --assemblies <a,b>       weaver only: assemblies to weave; default inferred
              --snapshots <n>          heap only: how many, --snapshot-interval <seconds> apart
              --symbols <dir>          build output, so results carry source locations
              --sessions-root <dir>    where the session database is written
              --name <text>            a name for the session directory

            The service prints {"port":...} on stdout once it is listening; POST /shutdown ends it.
            """);
        return command is "help" or "--help" or "-h" ? 0 : 2;
}

static string? Option(string[] args, string name)
{
    int i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static async Task<int> ServeAsync(int port, string? sessionsRoot)
{
    await using var service = new ControlService(port, sessionsRoot);
    try
    {
        service.Start();
    }
    catch (System.Net.HttpListenerException e)
    {
        Console.Error.WriteLine($"nap serve: cannot listen on 127.0.0.1:{service.Port}: {e.Message}");
        return 1;
    }

    // The GUI spawns this process with no port and reads the chosen one from here.
    Console.WriteLine(JsonSerializer.Serialize(new PortLine(service.Port, service.SessionsRoot), NapJsonContext.Default.PortLine));
    Console.Out.Flush();

    using var stopping = CancellationTokenSource.CreateLinkedTokenSource(service.Stopping);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
    try { await Task.Delay(Timeout.InfiniteTimeSpan, stopping.Token); }
    catch (OperationCanceledException) { /* asked to stop */ }
    Console.Error.WriteLine("nap serve: stopped");
    return 0;
}

/// <summary>
/// One session from the command line: the same Core path the MCP server and the GUI
/// drive, with the arguments a script can pass. Progress goes to stderr so that stdout
/// stays a single JSON object a caller can parse.
/// </summary>
static async Task<int> RunAsync(string[] args)
{
    string? package = Option(args, "--package");
    if (string.IsNullOrWhiteSpace(package))
    {
        Console.Error.WriteLine("nap run needs --package <application id>.");
        return 2;
    }

    string? serial = Option(args, "--device");
    var adb = new AdbClient();
    var devices = await adb.ListDevicesAsync(CancellationToken.None);
    if (serial is null)
    {
        if (devices.Count != 1)
        {
            Console.Error.WriteLine(devices.Count == 0
                ? "No device is attached."
                : $"{devices.Count} devices are attached: pass --device <serial>.");
            return 2;
        }
        serial = devices[0].Serial;
    }

    string mode = Option(args, "--mode") ?? "sampling";
    var spec = SessionSpecFactory.Build(
        serial,
        package!,
        mode,
        Option(args, "--launch") ?? "restart",
        int.TryParse(Option(args, "--duration"), out int seconds) ? seconds : (mode == "heap" ? null : 20),
        Option(args, "--callspec"),
        trackAllocations: !args.Contains("--no-allocations", StringComparer.OrdinalIgnoreCase),
        name: Option(args, "--name"),
        engine: Option(args, "--engine") ?? "auto",
        weaveAssemblies: Split(Option(args, "--assemblies")),
        snapshots: int.TryParse(Option(args, "--snapshots"), out int snapshots) ? snapshots : 1,
        snapshotIntervalSeconds: int.TryParse(Option(args, "--snapshot-interval"), out int interval) ? interval : 30,
        symbolsDir: Option(args, "--symbols"));

    var registry = new SessionRegistry(Option(args, "--sessions-root"), line => Console.Error.WriteLine(line));
    var live = registry.Create(spec);
    SessionInfo info;
    try
    {
        info = await live.Session.RunAsync(CancellationToken.None);
    }
    catch (Exception e) when (e is ProfilerException or ToolException)
    {
        // These carry guidance a user can act on; a stack trace buries it.
        Console.Error.WriteLine(e.Message);
        return 1;
    }

    Console.WriteLine(JsonSerializer.Serialize(
        new RunResult(info.Id, info.State.ToString(), info.DatabasePath, info.Error, info.Warnings),
        NapJsonContext.Default.RunResult));
    return info.State == SessionState.Ready ? 0 : 1;
}

/// <summary>
/// Where the external tools come from. Support questions about this profiler are
/// mostly "which adb / which dsrouter did it use", and guessing is expensive.
/// </summary>
static int Doctor()
{
    string? adb = ToolLocator.FindAdb();
    string? dsrouter = ToolLocator.FindDsRouter();
    string appBase = AppContext.BaseDirectory;

    Console.WriteLine($"nap {ProfilerSession.ToolVersion}");
    Console.WriteLine($"  running from   {appBase}");
    Console.WriteLine($"  adb            {adb ?? "NOT FOUND - install the Android platform-tools or set ANDROID_HOME"}");
    Console.WriteLine($"  dsrouter       {dsrouter ?? "NOT FOUND - dotnet tool install -g dotnet-dsrouter"}");
    if (dsrouter is not null)
    {
        // The package is bin/ plus its siblings, so "ours" means anywhere under the
        // parent of the directory this executable runs from.
        string packageRoot = Path.GetFullPath(Path.Combine(appBase, ".."));
        bool packaged = Path.GetFullPath(dsrouter).StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"                 ({(packaged ? "from this package" : "from this machine")})");
    }
    return adb is not null && dsrouter is not null ? 0 : 1;
}

static IReadOnlyList<string>? Split(string? value) =>
    string.IsNullOrWhiteSpace(value) ? null : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

// Anonymous types cannot be source-generated, and reflection-based serialization does
// not exist in a Native AOT build: what nap prints is declared here and generated below.
record PortLine(int Port, string SessionsRoot);
record RunResult(string Id, string State, string DatabasePath, string? Error, IReadOnlyList<string> Warnings);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(IReadOnlyList<DeviceInfo>))]
[JsonSerializable(typeof(PortLine))]
[JsonSerializable(typeof(RunResult))]
internal sealed partial class NapJsonContext : JsonSerializerContext;
