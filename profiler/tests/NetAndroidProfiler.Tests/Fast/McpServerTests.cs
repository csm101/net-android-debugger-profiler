using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NetAndroidProfiler.Core.Analysis;
using NetAndroidProfiler.Core.Sessions;
using NetAndroidProfiler.Core.Store;

namespace NetAndroidProfiler.Tests.Fast;

/// <summary>
/// Drives the MCP server as a real process over stdio (no device): a sessions
/// root is prepared from a recorded trace, then the read-only tools are called
/// through JSON-RPC exactly as an MCP client would.
/// </summary>
public sealed class McpServerTests : IDisposable
{
    private readonly McpServerFixture _server;
    private readonly string _sessionsRoot;
    private const string PreparedSessionId = "20260101-000000-mcp.test-sampling";

    public McpServerTests()
    {
        _sessionsRoot = Path.Combine(Path.GetTempPath(), "net-android-profiler-tests", "mcp", Guid.NewGuid().ToString("N"));
        PrepareSession(Path.Combine(_sessionsRoot, PreparedSessionId));
        _server = new McpServerFixture(_sessionsRoot);
    }

    public void Dispose() => _server.Dispose();

    /// <summary>Build a Ready sampling session on disk from the recorded trace.</summary>
    private static void PrepareSession(string dir)
    {
        Directory.CreateDirectory(dir);
        var spec = new SessionSpec("emulator-test", "mcp.test", NetAndroidProfiler.Core.Apps.ProfilingMode.Sampling);
        File.WriteAllText(Path.Combine(dir, "session.json"), JsonSerializer.Serialize(spec, new JsonSerializerOptions { WriteIndented = true }));
        var r = new SamplingAnalyzer().Analyze(Recorded.SamplingJit20s);
        using var store = ResultStore.Create(Path.Combine(dir, "session.db"), "test");
        store.WriteSampling(r);
        store.WriteSession(new SessionRow(Path.GetFileName(dir), "Sampling", "Ready", "mcp.test", "emulator-test",
            DateTimeOffset.UtcNow, 20000, "trace.nettrace", r.TotalSamples, r.SamplesWithStack, null, null));
    }

    [Fact]
    public void Initialize_and_tools_list_expose_the_P1_tool_surface()
    {
        var init = _server.Call("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "0" } });
        Assert.Equal("net-android-profiler", init.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        _server.Notify("notifications/initialized");

        var tools = _server.Call("tools/list", new { }).GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToHashSet();
        foreach (var expected in new[]
        {
            "list_devices", "check_app", "profile_run", "profile_start", "profile_stop", "profile_status",
            "profile_sessions", "profile_hotspots", "profile_flat", "profile_tree", "profile_callers",
            "profile_callees", "profile_timings", "alloc_report", "heap_report", "profile_threads",
            "profile_report", "profile_annotate_source", "get_app_output",
        })
            Assert.Contains(expected, tools);
    }

    [Fact]
    public void Read_only_tools_answer_from_the_prepared_session()
    {
        _server.Initialize();

        string sessions = _server.CallTool("profile_sessions", new { max = 5 });
        Assert.Contains(PreparedSessionId, sessions);
        Assert.Contains("ready", sessions);

        string hot = _server.CallTool("profile_hotspots", new { sessionId = PreparedSessionId, top = 5 });
        Assert.Contains("CpuBurner.Busy", hot);

        string report = _server.CallTool("profile_report", new { sessionId = PreparedSessionId });
        Assert.Contains("mode=Sampling", report);
        Assert.Contains("Top exclusive CPU", report);

        string tree = _server.CallTool("profile_tree", new { sessionId = PreparedSessionId });
        Assert.Contains("thread", tree);

        string callers = _server.CallTool("profile_callers", new { sessionId = PreparedSessionId, method = "CpuBurner.Busy" });
        Assert.Contains("WorkloadRunner.Loop", callers);

        string threads = _server.CallTool("profile_threads", new { sessionId = PreparedSessionId });
        Assert.Contains("samples=", threads);
    }

    [Fact]
    public void Error_paths_return_mcp_errors_instead_of_hanging()
    {
        _server.Initialize();

        var unknownSession = _server.CallToolRaw("profile_hotspots", new { sessionId = "does-not-exist" });
        Assert.True(IsError(unknownSession), "unknown session must be reported as an error");

        var unknownMethod = _server.CallToolRaw("profile_callers", new { sessionId = PreparedSessionId, method = "No.Such.Method" });
        Assert.True(IsError(unknownMethod), "unknown method must be reported as an error");

        var badMode = _server.CallToolRaw("profile_run", new { deviceSerial = "emulator-test", packageName = "mcp.test", mode = "nonsense" });
        Assert.True(IsError(badMode), "an unknown mode must be reported as an error");

        // The server must still be alive and answering after the failures.
        Assert.Contains(PreparedSessionId, _server.CallTool("profile_sessions", new { max = 5 }));
    }

    private static bool IsError(JsonElement response) =>
        response.TryGetProperty("error", out _) ||
        (response.TryGetProperty("result", out var result) && result.TryGetProperty("isError", out var flag) && flag.GetBoolean());

    /// <summary>The MCP server process plus a minimal JSON-RPC client over its stdio.</summary>
    private sealed class McpServerFixture : IDisposable
    {
        private readonly Process _process;
        private int _nextId = 1;

        public McpServerFixture(string sessionsRoot)
        {
            string dll = LocateServerDll();
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
            psi.ArgumentList.Add(dll);
            psi.Environment["NAP_SESSIONS_ROOT"] = sessionsRoot;
            _process = Process.Start(psi) ?? throw new InvalidOperationException("cannot start the MCP server");
            _process.ErrorDataReceived += (_, _) => { };
            _process.BeginErrorReadLine();
        }

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
            var deadline = DateTime.UtcNow.AddSeconds(60);
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
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
            _process.Dispose();
        }
    }
}
