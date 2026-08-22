using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NetAndroidProfiler.Tests.Support;

/// <summary>
/// The MCP server as a real process plus a minimal JSON-RPC client over its stdio: the
/// only way to test what a client actually gets, rather than what the tool methods
/// return in process. Shared by the fast tests (prepared sessions, no device) and the
/// device tests (real runs).
/// </summary>
public sealed class McpServerFixture : IDisposable
{
    private readonly Process _process;
    private int _nextId = 1;

    public McpServerFixture(string sessionsRoot)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(LocateServerDll());
        psi.Environment["NAP_SESSIONS_ROOT"] = sessionsRoot;
        _process = Process.Start(psi) ?? throw new InvalidOperationException("cannot start the MCP server");
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginErrorReadLine();
    }

    /// <summary>How long to wait for a response; a device run needs far longer than a query.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    private static string LocateServerDll()
    {
        // tests/<proj>/bin/<cfg>/<tfm> -> repository root
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NetAndroidProfiler.slnx")))
            dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("repository root not found from " + AppContext.BaseDirectory);
        string configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        string dll = Path.Combine(dir.FullName, "src", "NetAndroidProfiler.Mcp", "bin", configuration, "net10.0", "NetAndroidProfiler.Mcp.dll");
        if (!File.Exists(dll)) throw new FileNotFoundException("MCP server not built", dll);
        return dll;
    }

    public void Initialize()
    {
        Call("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "0" } });
        Notify("notifications/initialized");
    }

    public JsonElement Call(string method, object parameters)
    {
        int id = _nextId++;
        Send(new { jsonrpc = "2.0", id, method, @params = parameters });
        return ReadResponse(id);
    }

    public void Notify(string method) => Send(new { jsonrpc = "2.0", method });

    /// <summary>Call a tool and return its text content (throws when the call reports an error).</summary>
    public string CallTool(string name, object arguments)
    {
        var response = CallToolRaw(name, arguments);
        if (IsError(response))
            throw new InvalidOperationException($"tool {name} failed: {response}");
        return Text(response);
    }

    public JsonElement CallToolRaw(string name, object arguments) =>
        Call("tools/call", new { name, arguments });

    public static bool IsError(JsonElement response) =>
        response.TryGetProperty("error", out _) ||
        (response.TryGetProperty("result", out var result) && result.TryGetProperty("isError", out var flag) && flag.GetBoolean());

    private static string Text(JsonElement response) =>
        string.Concat(response.GetProperty("result").GetProperty("content").EnumerateArray()
            .Where(c => c.GetProperty("type").GetString() == "text")
            .Select(c => c.GetProperty("text").GetString()));

    private void Send(object message)
    {
        _process.StandardInput.WriteLine(JsonSerializer.Serialize(message));
        _process.StandardInput.Flush();
    }

    private JsonElement ReadResponse(int id)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            string? line = _process.StandardOutput.ReadLine();
            if (line is null) throw new InvalidOperationException("the MCP server closed its output");
            if (line.Length == 0 || line[0] != '{') continue;
            var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("id", out var responseId) && responseId.TryGetInt32(out int value) && value == id)
                return doc.RootElement.Clone();
        }
        throw new TimeoutException($"no response for request {id}");
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(5000)) _process.Kill(entireProcessTree: true);
            }
        }
        catch { /* the server is going away either way */ }
        _process.Dispose();
    }
}
