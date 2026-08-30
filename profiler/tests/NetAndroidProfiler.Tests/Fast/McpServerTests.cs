using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NetAndroidProfiler.Core.Analysis;
using NetAndroidProfiler.Core.Sessions;
using NetAndroidProfiler.Core.Store;
using NetAndroidProfiler.Tests.Support;

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

    /// The sources list_app_projects is pointed at: this repository's own test app.
    private static string TestTargetFolder
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NetAndroidProfiler.slnx")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "TestTarget");
        }
    }

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
            "list_devices", "check_app", "list_app_projects", "profile_run", "profile_start", "profile_stop", "profile_status",
            "profile_sessions", "profile_archive", "profile_archives", "profile_hotspots", "profile_flat", "profile_tree", "profile_callers",
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

        // The tool that turns "here are the sources" into the arguments profile_run needs.
        string projects = _server.CallTool("list_app_projects", new { path = TestTargetFolder });
        Assert.Contains("com.mcasoftware.testtarget", projects);
        Assert.Contains("symbolsDir", projects);

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

        var badPath = _server.CallToolRaw("list_app_projects", new { path = Path.Combine(Path.GetTempPath(), "nap-no-such-folder") });
        Assert.True(IsError(badPath), "a path that does not exist must be reported as an error");

        // The server must still be alive and answering after the failures.
        Assert.Contains(PreparedSessionId, _server.CallTool("profile_sessions", new { max = 5 }));
    }

    private static bool IsError(JsonElement response) => McpServerFixture.IsError(response);
}
