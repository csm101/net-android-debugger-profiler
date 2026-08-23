using System.Text.Json;
using NetAndroidProfiler.Tests.Support;

namespace NetAndroidProfiler.Tests.Device;

/// <summary>
/// The MCP surface against a real device, driven the way an agent drives it: over stdio,
/// one JSON-RPC call at a time. The fast MCP tests prove the protocol and the read-only
/// tools; these prove the three tools that actually touch a device - profile_run, the
/// profile_start/profile_stop pair, and profile_annotate_source on what they produced.
///
/// Needs TestTarget installed as a Debug build with EnableDiagnostics=true, like the
/// other device tests.
/// </summary>
[Trait("Category", "Device")]
[Collection("device")]
public sealed class McpDeviceTests : IDisposable
{
    private static string Serial => Environment.GetEnvironmentVariable("NAP_TEST_SERIAL") ?? "emulator-5556";
    private static string Package => Environment.GetEnvironmentVariable("NAP_TEST_PACKAGE") ?? "com.mcasoftware.testtarget";
    private static string SessionsRoot => Path.Combine(Path.GetTempPath(), "net-android-profiler-tests", "mcp-device");

    private readonly McpServerFixture _server;

    public McpDeviceTests()
    {
        Directory.CreateDirectory(SessionsRoot);
        _server = new McpServerFixture(SessionsRoot) { Timeout = TimeSpan.FromMinutes(6) };
        _server.Initialize();
    }

    public void Dispose() => _server.Dispose();

    /// <summary>The app's build output, so the session records where each method lives.</summary>
    private static string SymbolsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NetAndroidProfiler.slnx")))
            dir = dir.Parent;
        string bin = Path.Combine(dir!.FullName, "TestTarget", "bin", "Debug");
        return Directory.Exists(bin)
            ? Directory.GetDirectories(bin).FirstOrDefault(d => File.Exists(Path.Combine(d, "TestTarget.pdb"))) ?? bin
            : bin;
    }

    [Fact]
    public void Profile_run_produces_a_session_the_read_only_tools_can_answer_from()
    {
        string summary = _server.CallTool("profile_run", new
        {
            deviceSerial = Serial,
            packageName = Package,
            mode = "sampling",
            durationSeconds = 12,
            symbolsDir = SymbolsDir(),
        });

        Assert.Contains("Sampling", summary);
        Assert.Contains("samples", summary, StringComparison.OrdinalIgnoreCase);

        // The tool that ran it and the tools that read it must agree on which session is
        // current: an agent calls profile_hotspots straight after profile_run, with no id.
        string hotspots = _server.CallTool("profile_hotspots", new { top = 10 });
        Assert.Contains("TestTarget", hotspots);

        string sessions = _server.CallTool("profile_sessions", new { max = 3 });
        Assert.Contains(Package, sessions);
        Assert.Contains("ready", sessions);
    }

    [Fact]
    public void Profile_start_and_profile_stop_round_trip()
    {
        // profile_start returns as soon as the session exists, and profile_status is a
        // read of memory: seconds, not minutes. Waiting six minutes for either would only
        // turn a wedged server into a slow-looking one.
        _server.Timeout = TimeSpan.FromSeconds(45);
        string started = _server.CallTool("profile_start", new
        {
            deviceSerial = Serial,
            packageName = Package,
            mode = "sampling",
        });
        Assert.Contains(Package, started);

        // profile_status is what an agent polls while the app does its work.
        string state = "";
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            state = _server.CallTool("profile_status", new { });
            if (state.Contains("Collecting", StringComparison.OrdinalIgnoreCase)) break;
            Assert.DoesNotContain("Failed", state);
            Thread.Sleep(1000);
        }
        Assert.Contains("Collecting", state, StringComparison.OrdinalIgnoreCase);

        Thread.Sleep(TimeSpan.FromSeconds(8));

        // Stopping collects, analyses and writes the database: this one really can take
        // minutes on a busy app.
        _server.Timeout = TimeSpan.FromMinutes(5);
        string stopped = _server.CallTool("profile_stop", new { });
        _server.Timeout = TimeSpan.FromSeconds(45);
        Assert.Contains("Sampling", stopped);
        Assert.DoesNotContain("Failed", stopped);

        // A stopped session is a readable one, without naming it.
        Assert.Contains("TestTarget", _server.CallTool("profile_hotspots", new { top = 10 }));
    }

    [Fact]
    public void Profile_annotate_source_puts_the_figures_beside_the_method()
    {
        _server.CallTool("profile_run", new
        {
            deviceSerial = Serial,
            packageName = Package,
            mode = "sampling",
            durationSeconds = 12,
            symbolsDir = SymbolsDir(),
        });

        string annotated = _server.CallTool("profile_annotate_source", new
        {
            symbolsDir = SymbolsDir(),
            sourceFile = "Workloads/CpuBurner.cs",
        });

        Assert.Contains("Busy", annotated);
        // MonoVM gives no per-line samples: the figures sit on the method's first line.
        Assert.Contains("samples", annotated, StringComparison.OrdinalIgnoreCase);

        // A file the pdbs do not know about is an error with guidance, not an empty answer.
        var missing = _server.CallToolRaw("profile_annotate_source", new
        {
            symbolsDir = SymbolsDir(),
            sourceFile = "Nowhere/DoesNotExist.cs",
        });
        Assert.True(McpServerFixture.IsError(missing), "an unknown source file must be reported as an error");
    }
}
