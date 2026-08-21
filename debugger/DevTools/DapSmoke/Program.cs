using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

// Drives the DAP adapter against a real app, the way an editor would, and prints what it saw.
// Unlike the end-to-end tests (which use TestTarget), this points at any installed app - it exists
// to try the adapter against the reference application without republishing the MCP server.
//
//   dotnet run --project DevTools/DapSmoke -- <serial> <package> <file> <line> [--keep-fresh]
//
// Exit code 0 when every step answered, 1 otherwise.

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

if (args.Length < 4)
{
    Console.Error.WriteLine("usage: DapSmoke <deviceSerial> <packageName> <sourceFile> <line> [--keep-fresh]");
    return 2;
}

var serial = args[0];
var package = args[1];
var file = args[2];
var line = int.Parse(args[3]);
var keepFresh = args.Contains("--keep-fresh");

var repoRoot = FindRepoRoot();
var adapter = Path.Combine(repoRoot, "src", "NetAndroidDebugger.Dap", "bin", "Debug", "net10.0", "NetAndroidDebugger.Dap.dll");
if (!File.Exists(adapter))
{
    Console.Error.WriteLine($"adapter not built: {adapter}");
    return 2;
}

var client = new Client(adapter);
var failures = 0;

try
{
    var caps = await client.ExpectAsync("initialize", new JsonObject { ["adapterID"] = "dap-smoke" });
    Console.WriteLine($"initialize: {caps?.Count ?? 0} capabilities");
    await client.WaitForEventAsync("initialized", TimeSpan.FromSeconds(10));

    var bps = await client.ExpectAsync("setBreakpoints", new JsonObject
    {
        ["source"] = new JsonObject { ["path"] = file },
        ["breakpoints"] = new JsonArray(new JsonObject { ["line"] = line }),
    });
    Console.WriteLine($"setBreakpoints: {bps?["breakpoints"]?.AsArray().Count ?? 0} reported");

    var launch = new JsonObject { ["deviceSerial"] = serial, ["packageName"] = package };
    if (keepFresh) launch["keepPropertyFresh"] = true;
    await client.ExpectAsync("launch", launch);
    await client.ExpectAsync("configurationDone", null);
    Console.WriteLine($"launched {package} on {serial}");

    var stopped = await client.WaitForEventAsync("stopped", TimeSpan.FromSeconds(120));
    if (stopped is null)
    {
        Console.Error.WriteLine("FAIL: no stopped event - the breakpoint was never hit");
        failures++;
    }
    else
    {
        var body = stopped["body"]!.AsObject();
        var threadId = body["threadId"]!.GetValue<int>();
        Console.WriteLine($"stopped: reason={body["reason"]} thread={threadId}");

        var threads = (await client.ExpectAsync("threads", null))?["threads"]?.AsArray();
        Console.WriteLine($"threads: {threads?.Count ?? 0}");

        var frames = (await client.ExpectAsync("stackTrace", new JsonObject { ["threadId"] = threadId }))?["stackFrames"]?.AsArray();
        Console.WriteLine($"stackTrace: {frames?.Count ?? 0} frames, top = {frames?[0]?["name"]} at {frames?[0]?["line"]}");

        var frameId = frames?[0]?["id"]?.GetValue<int>() ?? 0;
        var scopes = (await client.ExpectAsync("scopes", new JsonObject { ["frameId"] = frameId }))?["scopes"]?.AsArray();
        var localsRef = scopes?[0]?["variablesReference"]?.GetValue<int>() ?? 0;

        var locals = (await client.ExpectAsync("variables", new JsonObject { ["variablesReference"] = localsRef }))?["variables"]?.AsArray();
        Console.WriteLine($"locals: {locals?.Count ?? 0}");
        foreach (var local in locals ?? new JsonArray())
            Console.WriteLine($"  {local?["name"]} : {local?["type"]} = {Trim(local?["value"]?.GetValue<string>())}");

        // Expand the first expandable local: that is where the invocation-heavy paths live.
        var expandable = locals?.FirstOrDefault(v => (v?["variablesReference"]?.GetValue<int>() ?? 0) > 0);
        if (expandable is not null)
        {
            var reference = expandable["variablesReference"]!.GetValue<int>();
            var members = (await client.ExpectAsync("variables", new JsonObject { ["variablesReference"] = reference }))?["variables"]?.AsArray();
            Console.WriteLine($"expanded {expandable["name"]}: {members?.Count ?? 0} members");
            foreach (var m in (members ?? new JsonArray()).Take(10))
                Console.WriteLine($"  {m?["name"]} : {m?["type"]} = {Trim(m?["value"]?.GetValue<string>())}");
        }
        else
        {
            Console.WriteLine("no expandable local at this stop");
        }

        var evaluated = await client.SendAsync("evaluate", new JsonObject
        {
            ["expression"] = "1 + 1",
            ["frameId"] = frameId,
            ["context"] = "repl",
        });
        Console.WriteLine($"evaluate 1+1: success={evaluated["success"]} result={evaluated["body"]?["result"]}");

        await client.ExpectAsync("continue", new JsonObject { ["threadId"] = threadId });
        Console.WriteLine("continued");
    }

    await client.ExpectAsync("disconnect", new JsonObject { ["terminateDebuggee"] = true });
    Console.WriteLine("disconnected");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex.Message}");
    failures++;
}
finally
{
    await client.DisposeAsync();
}

Console.WriteLine(failures == 0 ? "OK" : $"{failures} failure(s)");
return failures == 0 ? 0 : 1;

static string Trim(string? s) => s is null ? "(null)" : s.Length <= 100 ? s : s[..100] + "...";

static string FindRepoRoot()
{
    var dir = AppContext.BaseDirectory;
    while (dir is not null && !File.Exists(Path.Combine(dir, "NetAndroidDebugger.slnx")))
        dir = Path.GetDirectoryName(dir);
    return dir ?? Directory.GetCurrentDirectory();
}

/// <summary>The smallest DAP client that can drive a scripted session.</summary>
file sealed class Client : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private readonly List<JsonObject> _events = new();
    private int _seq;

    public Client(string adapterDll)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(adapterDll);
        _process = Process.Start(psi) ?? throw new InvalidOperationException("could not start the adapter");
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine("[adapter] " + e.Data); };
        _process.BeginErrorReadLine();
        _stdin = _process.StandardInput.BaseStream;
        _stdout = _process.StandardOutput.BaseStream;
    }

    public async Task<JsonObject> SendAsync(string command, JsonObject? arguments)
    {
        var seq = ++_seq;
        var request = new JsonObject { ["seq"] = seq, ["type"] = "request", ["command"] = command };
        if (arguments is not null) request["arguments"] = arguments;
        await WriteAsync(request);

        while (true)
        {
            var message = await ReadAsync() ?? throw new InvalidOperationException($"the adapter closed during '{command}'");
            if (message["type"]?.GetValue<string>() == "event") { _events.Add(message); continue; }
            if (message["request_seq"]?.GetValue<int>() == seq) return message;
        }
    }

    public async Task<JsonObject?> ExpectAsync(string command, JsonObject? arguments)
    {
        var response = await SendAsync(command, arguments);
        if (!(response["success"]?.GetValue<bool>() ?? false))
            throw new InvalidOperationException($"{command} failed: {response["message"]?.GetValue<string>()}");
        return response["body"] as JsonObject;
    }

    public async Task<JsonObject?> WaitForEventAsync(string name, TimeSpan timeout)
    {
        var already = _events.FirstOrDefault(e => e["event"]?.GetValue<string>() == name);
        if (already is not null) { _events.Remove(already); return already; }

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var message = await ReadAsync(cts.Token);
                if (message is null) return null;
                if (message["type"]?.GetValue<string>() != "event") continue;
                if (message["event"]?.GetValue<string>() == name) return message;
                _events.Add(message);
            }
        }
        catch (OperationCanceledException) { }
        return null;
    }

    private async Task WriteAsync(JsonObject message)
    {
        var payload = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _stdin.WriteAsync(Encoding.UTF8.GetBytes($"Content-Length: {payload.Length}\r\n\r\n"));
        await _stdin.WriteAsync(payload);
        await _stdin.FlushAsync();
    }

    private async Task<JsonObject?> ReadAsync(CancellationToken ct = default)
    {
        int? length = null;
        var line = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            if (await _stdout.ReadAsync(one.AsMemory(0, 1), ct) == 0) return null;
            if (one[0] != (byte)'\n') { if (one[0] != (byte)'\r') line.Append((char)one[0]); continue; }
            if (line.Length == 0) break;
            const string header = "Content-Length:";
            var text = line.ToString();
            line.Clear();
            if (text.StartsWith(header, StringComparison.OrdinalIgnoreCase)) length = int.Parse(text[header.Length..].Trim());
        }
        if (length is null) return null;

        var buffer = new byte[length.Value];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await _stdout.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) return null;
            read += n;
        }
        return JsonNode.Parse(Encoding.UTF8.GetString(buffer)) as JsonObject;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _stdin.Close();
                if (!_process.WaitForExit(10000)) _process.Kill(entireProcessTree: true);
            }
        }
        catch { /* the adapter is going away either way */ }
        _process.Dispose();
        await Task.CompletedTask;
    }
}
