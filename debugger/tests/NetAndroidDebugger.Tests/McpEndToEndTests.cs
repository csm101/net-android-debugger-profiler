using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NetAndroidDebugger.Tests.Harness;
using Xunit.Abstractions;

namespace NetAndroidDebugger.Tests;

/// <summary>Drives the real MCP server process over stdio with the SDK client, against TestTarget.</summary>
[Collection(DeviceCollection.Name)]
public sealed class McpEndToEndTests(DeviceFixture device, ITestOutputHelper output)
{
    private static string ServerDll =>
        Path.Combine(TestEnvironment.RepoRoot, "src", "NetAndroidDebugger.Mcp", "bin", BuildConfiguration, "net10.0", "NetAndroidDebugger.Mcp.dll");

    private const string BuildConfiguration =
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    private async Task<McpClient> ConnectAsync(CancellationToken ct)
    {
        Assert.True(File.Exists(ServerDll), $"MCP server not built: {ServerDll}");
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "net-android-debugger",
            Command = "dotnet",
            Arguments = [ServerDll],
        });
        return await McpClient.CreateAsync(transport, cancellationToken: ct);
    }

    private async Task<string> CallAsync(McpClient client, string tool, IReadOnlyDictionary<string, object?>? args, CancellationToken ct)
    {
        var result = await client.CallToolAsync(tool, args, cancellationToken: ct);
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        output.WriteLine($"[{tool}] {(result.IsError == true ? "ERROR " : "")}{text}");
        Assert.False(result.IsError == true, $"{tool} returned an error: {text}");
        return text;
    }

    [Fact]
    public async Task ToolList_ContainsCoreTools()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await using var client = await ConnectAsync(cts.Token);
        var tools = (await client.ListToolsAsync(cancellationToken: cts.Token)).Select(t => t.Name).ToHashSet();
        foreach (var expected in new[] { "list_devices", "launch_app", "set_breakpoint", "continue_and_wait", "wait_until_stopped", "get_locals", "get_call_stack", "step_over", "evaluate_expression", "get_compact_debug_snapshot", "terminate_app" })
            Assert.Contains(expected, tools);
    }

    [Fact]
    public async Task Roundtrip_Launch_Breakpoint_Wait_Locals_Snapshot_Terminate()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var client = await ConnectAsync(cts.Token);
        var ct = cts.Token;

        var status0 = await CallAsync(client, "get_debug_session_status", null, ct);
        Assert.Contains("No session", status0);

        var launched = await CallAsync(client, "launch_app", new Dictionary<string, object?>
        {
            ["deviceSerial"] = device.Serial,
            ["packageName"] = TestEnvironment.TestTargetPackage,
        }, ct);
        Assert.Contains("state=Running", launched);

        var tickLine = TestEnvironment.LineOf(TestEnvironment.MainActivitySource, "Android.Util.Log.Debug(\"TestTarget\", message);");
        var bp = await CallAsync(client, "set_breakpoint", new Dictionary<string, object?>
        {
            ["file"] = TestEnvironment.MainActivitySource,
            ["line"] = tickLine,
        }, ct);
        Assert.StartsWith("bp 1 ", bp);

        var stop = await CallAsync(client, "wait_until_stopped", new Dictionary<string, object?> { ["timeoutSeconds"] = 30 }, ct);
        Assert.StartsWith("Stopped:", stop);
        Assert.Contains("reason=Breakpoint", stop);
        Assert.Contains($"MainActivity.cs:{tickLine}", stop);

        var locals = await CallAsync(client, "get_locals", null, ct);
        Assert.Contains("message : string = \"tick ", locals);
        Assert.Contains("now : long", locals);

        var stack = await CallAsync(client, "get_call_stack", null, ct);
        Assert.Contains("#0 TestTarget.MainActivity.Tick", stack);

        var eval = await CallAsync(client, "evaluate_expression", new Dictionary<string, object?> { ["expression"] = "now + 1" }, ct);
        Assert.Contains(" : long = ", eval);

        var snapshot = await CallAsync(client, "get_compact_debug_snapshot", null, ct);
        Assert.Contains("-- call stack", snapshot);
        Assert.Contains("-- locals", snapshot);

        var next = await CallAsync(client, "continue_and_wait", new Dictionary<string, object?> { ["timeoutSeconds"] = 30 }, ct);
        Assert.StartsWith("Stopped:", next);

        var bye = await CallAsync(client, "terminate_app", null, ct);
        Assert.Contains("Terminated", bye);

        var status1 = await CallAsync(client, "get_debug_session_status", null, ct);
        Assert.Contains("No session", status1);
    }

    [Fact]
    public async Task SecondLaunch_ReplacesTheFirstSession()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var client = await ConnectAsync(cts.Token);
        var ct = cts.Token;
        var args = new Dictionary<string, object?>
        {
            ["deviceSerial"] = device.Serial,
            ["packageName"] = TestEnvironment.TestTargetPackage,
        };

        var first = await CallAsync(client, "launch_app", args, ct);
        var firstPid = ExtractMainPid(first);

        // Launching again must close the previous session rather than pile up on it.
        var second = await CallAsync(client, "launch_app", args, ct);
        var secondPid = ExtractMainPid(second);
        Assert.NotEqual(firstPid, secondPid);

        var status = await CallAsync(client, "get_debug_session_status", null, ct);
        Assert.Contains($"pid={secondPid}", status);
        Assert.DoesNotContain($"pid={firstPid}", status);
        Assert.Contains("Terminated", await CallAsync(client, "terminate_app", null, ct));
    }

    private static int ExtractMainPid(string statusText)
    {
        var line = statusText.Split('\n').First(l => l.Contains("process pid=") && l.Contains($"name={TestEnvironment.TestTargetPackage} "));
        var token = line.Split("pid=")[1].Split(' ')[0];
        return int.Parse(token);
    }

    [Fact]
    public async Task ErrorPaths_ReturnToolErrors_NeverHang()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var client = await ConnectAsync(cts.Token);
        var ct = cts.Token;

        async Task<string> ExpectError(string tool, IReadOnlyDictionary<string, object?>? args)
        {
            var result = await client.CallToolAsync(tool, args, cancellationToken: ct);
            var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
            output.WriteLine($"[{tool}] isError={result.IsError} {text}");
            Assert.True(result.IsError == true, $"{tool} should have failed, got: {text}");
            Assert.False(string.IsNullOrWhiteSpace(text));
            return text;
        }

        // No session yet.
        Assert.Contains("launch_app", await ExpectError("get_locals", null));
        Assert.Contains("launch_app", await ExpectError("continue_and_wait", new Dictionary<string, object?> { ["timeoutSeconds"] = 1 }));
        // Unknown device.
        await ExpectError("launch_app", new Dictionary<string, object?> { ["deviceSerial"] = "no-such-device", ["packageName"] = TestEnvironment.TestTargetPackage });
        // Session exists but nothing is stopped: inspection must fail fast.
        await CallAsync(client, "launch_app", new Dictionary<string, object?> { ["deviceSerial"] = device.Serial, ["packageName"] = TestEnvironment.TestTargetPackage }, ct);
        await ExpectError("get_call_stack", null);
        await ExpectError("get_locals", new Dictionary<string, object?> { ["pid"] = 1, ["threadId"] = 1 });
        await ExpectError("expand_variable", new Dictionary<string, object?> { ["handle"] = "1:999" });
        Assert.Contains("Terminated", await CallAsync(client, "terminate_app", null, ct));
    }

    [Fact]
    public async Task SetBreakpoint_BeforeLaunch_IsHitOnStartupCode_AndWaitReturnsCurrentStop()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var client = await ConnectAsync(cts.Token);
        var ct = cts.Token;

        // OnCreate runs once at startup: only a breakpoint set before launch can catch it.
        var onCreateLine = TestEnvironment.LineOf(TestEnvironment.MainActivitySource, "SetContentView(Resource.Layout.activity_main);");
        var bp = await CallAsync(client, "set_breakpoint", new Dictionary<string, object?>
        {
            ["file"] = TestEnvironment.MainActivitySource,
            ["line"] = onCreateLine,
        }, ct);
        Assert.StartsWith("bp 1 ", bp);

        var launched = await CallAsync(client, "launch_app", new Dictionary<string, object?>
        {
            ["deviceSerial"] = device.Serial,
            ["packageName"] = TestEnvironment.TestTargetPackage,
        }, ct);
        Assert.Contains("package=" + TestEnvironment.TestTargetPackage, launched);

        var stop = await CallAsync(client, "wait_until_stopped", new Dictionary<string, object?> { ["timeoutSeconds"] = 30 }, ct);
        Assert.StartsWith("Stopped:", stop);
        Assert.Contains($"MainActivity.cs:{onCreateLine}", stop);
        Assert.Contains("OnCreate", stop);

        // Asking again without afterGeneration must report the same stop, not a timeout.
        var again = await CallAsync(client, "wait_until_stopped", new Dictionary<string, object?> { ["timeoutSeconds"] = 5 }, ct);
        Assert.StartsWith("Stopped:", again);
        Assert.Contains($"MainActivity.cs:{onCreateLine}", again);

        Assert.Contains("Terminated", await CallAsync(client, "terminate_app", null, ct));
    }
}
