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
//   nap version
using System.Text.Json;
using NetAndroidProfiler.Cli;
using NetAndroidProfiler.Core.Devices;
using NetAndroidProfiler.Core.Sessions;

var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
switch (command)
{
    case "serve":
        return await ServeAsync(Option(args, "--port") is { } p ? int.Parse(p) : 0, Option(args, "--sessions-root"));

    case "devices":
    {
        var devices = await new AdbClient().ListDevicesAsync(CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(devices, json));
        return 0;
    }

    case "version":
        Console.WriteLine(ProfilerSession.ToolVersion);
        return 0;

    default:
        Console.Error.WriteLine("""
            nap - .NET for Android profiler

              nap serve [--port <n>] [--sessions-root <dir>]   local control service (HTTP/JSON on 127.0.0.1)
              nap devices                                      attached devices as JSON
              nap version

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
    Console.WriteLine(JsonSerializer.Serialize(new { port = service.Port, sessionsRoot = service.SessionsRoot }));
    Console.Out.Flush();

    using var stopping = CancellationTokenSource.CreateLinkedTokenSource(service.Stopping);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
    try { await Task.Delay(Timeout.InfiniteTimeSpan, stopping.Token); }
    catch (OperationCanceledException) { /* asked to stop */ }
    Console.Error.WriteLine("nap serve: stopped");
    return 0;
}
