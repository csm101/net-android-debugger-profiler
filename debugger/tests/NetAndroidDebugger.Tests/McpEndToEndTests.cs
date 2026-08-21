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
    public async Task LaunchWithDeploy_BuildsInstallsAndAttaches()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        await using var client = await ConnectAsync(cts.Token);
        var ct = cts.Token;

        // The deploy path is the one a user hits first: point the tool at the csproj and let it
        // install before launching. An up-to-date build still exercises the whole route.
        var launched = await CallAsync(client, "launch_app", new Dictionary<string, object?>
        {
            ["deviceSerial"] = device.Serial,
            ["packageName"] = TestEnvironment.TestTargetPackage,
            ["projectPath"] = TestEnvironment.TestTargetProject,
            ["deploy"] = true,
        }, ct);
        Assert.Contains("state=Running", launched);
        Assert.Contains($"package={TestEnvironment.TestTargetPackage}", launched);

        var log = await CallAsync(client, "get_debugger_output", new Dictionary<string, object?> { ["maxLines"] = 2000 }, ct);
        Assert.Contains("deploy: ok", log);
        Assert.Contains("Terminated", await CallAsync(client, "terminate_app", null, ct));
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

    /// <summary>
    /// Walks the read-only and inspection tools in one stopped session. Their engine calls are
    /// covered by the Core tests; what this covers is the translation layer — parameter names,
    /// defaults, and the rendering — which is where a tool breaks without anything else noticing.
    /// </summary>
    [Fact]
    public async Task EveryInspectionTool_AnswersInAStoppedSession()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await using var client = await ConnectAsync(cts.Token);
        var ct = cts.Token;

        var tickLine = TestEnvironment.LineOf(TestEnvironment.MainActivitySource, "Android.Util.Log.Debug(\"TestTarget\", message);");
        await CallAsync(client, "set_breakpoint", new Dictionary<string, object?>
        {
            ["file"] = TestEnvironment.MainActivitySource,
            ["line"] = tickLine,
        }, ct);
        await CallAsync(client, "launch_app", new Dictionary<string, object?>
        {
            ["deviceSerial"] = device.Serial,
            ["packageName"] = TestEnvironment.TestTargetPackage,
        }, ct);
        var stop = await CallAsync(client, "wait_until_stopped", new Dictionary<string, object?> { ["timeoutSeconds"] = 60 }, ct);
        Assert.StartsWith("Stopped:", stop);

        var threads = await CallAsync(client, "get_threads", null, ct);
        Assert.Contains("Main", threads, StringComparison.Ordinal);

        var where = await CallAsync(client, "get_current_source_location", null, ct);
        Assert.Contains($"MainActivity.cs:{tickLine}", where);

        var one = await CallAsync(client, "get_variable", new Dictionary<string, object?> { ["name"] = "message" }, ct);
        Assert.Contains("message : string = \"tick ", one);

        var assemblies = await CallAsync(client, "get_loaded_assemblies", null, ct);
        Assert.Contains("TestTarget", assemblies, StringComparison.Ordinal);

        var sources = await CallAsync(client, "get_source_files", new Dictionary<string, object?> { ["file"] = "MainActivity.cs" }, ct);
        Assert.Contains(TestEnvironment.MainActivitySource, sources, StringComparison.OrdinalIgnoreCase);

        var breakpoints = await CallAsync(client, "list_breakpoints", null, ct);
        Assert.Contains($"MainActivity.cs:{tickLine}", breakpoints);

        var output = await CallAsync(client, "get_app_output", new Dictionary<string, object?>
        {
            ["maxLines"] = 50,
            ["contains"] = "tick",
        }, ct);
        Assert.Contains("tick", output, StringComparison.Ordinal);

        // Stepping, each shape of it, through the server rather than the engine.
        var into = await CallAsync(client, "step_into", new Dictionary<string, object?> { ["timeoutSeconds"] = 20 }, ct);
        Assert.StartsWith("Stopped:", into);
        var outOf = await CallAsync(client, "step_out", new Dictionary<string, object?> { ["timeoutSeconds"] = 20 }, ct);
        Assert.StartsWith("Stopped:", outOf);

        await CallAsync(client, "terminate_app", null, ct);
    }

    /// <summary>
    /// The tools that change how the session behaves rather than reporting on it. Each is called
    /// through the server and its effect is read back through the server.
    /// </summary>
    [Fact]
    public async Task SetupTools_TakeEffect_AndAreVisibleThroughTheServer()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await using var client = await ConnectAsync(cts.Token);
        var ct = cts.Token;

        var tickLine = TestEnvironment.LineOf(TestEnvironment.MainActivitySource, "Android.Util.Log.Debug(\"TestTarget\", message);");
        var nowLine = TestEnvironment.LineOf(TestEnvironment.MainActivitySource, "long now = Environment.TickCount64;");

        // set_breakpoints replaces every breakpoint of the file.
        await CallAsync(client, "set_breakpoint", new Dictionary<string, object?>
        {
            ["file"] = TestEnvironment.MainActivitySource,
            ["line"] = nowLine,
        }, ct);
        await CallAsync(client, "set_breakpoints", new Dictionary<string, object?>
        {
            ["file"] = TestEnvironment.MainActivitySource,
            ["lines"] = new[] { tickLine },
        }, ct);
        var listed = await CallAsync(client, "list_breakpoints", null, ct);
        Assert.Contains($"MainActivity.cs:{tickLine}", listed);
        Assert.DoesNotContain($"MainActivity.cs:{nowLine}", listed);

        var options = await CallAsync(client, "set_evaluation_options", new Dictionary<string, object?>
        {
            ["allowTargetInvoke"] = false,
            ["allowToStringCalls"] = false,
        }, ct);
        Assert.Contains("allowTargetInvoke=false", options);

        var filters = await CallAsync(client, "set_exception_filters", new Dictionary<string, object?>
        {
            ["firstChanceTypes"] = new[] { "System.InvalidOperationException" },
        }, ct);
        Assert.Contains("InvalidOperationException", filters, StringComparison.Ordinal);

        await CallAsync(client, "launch_app", new Dictionary<string, object?>
        {
            ["deviceSerial"] = device.Serial,
            ["packageName"] = TestEnvironment.TestTargetPackage,
        }, ct);
        var stop = await CallAsync(client, "wait_until_stopped", new Dictionary<string, object?> { ["timeoutSeconds"] = 60 }, ct);
        Assert.StartsWith("Stopped:", stop);

        // TestTarget throws a caught InvalidOperationException every fifth tick, so the stop may be
        // that exception or the breakpoint; the details tool only answers for the exception.
        if (stop.Contains("reason=Exception", StringComparison.Ordinal))
        {
            var details = await CallAsync(client, "get_exception_details", null, ct);
            Assert.Contains("InvalidOperationException", details, StringComparison.Ordinal);
        }

        var removed = await CallAsync(client, "remove_all_breakpoints", null, ct);
        Assert.Contains("removed", removed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No breakpoints", await CallAsync(client, "list_breakpoints", null, ct));

        await CallAsync(client, "terminate_app", null, ct);
    }

    /// <summary>
    /// remove_breakpoint takes the id set_breakpoint handed out, and pause_execution suspends a
    /// running app. Both are ordinary parts of a session that nothing else here exercises.
    /// </summary>
    [Fact]
    public async Task RemoveBreakpointById_AndPause_WorkThroughTheServer()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await using var client = await ConnectAsync(cts.Token);
        var ct = cts.Token;

        var nowLine = TestEnvironment.LineOf(TestEnvironment.MainActivitySource, "long now = Environment.TickCount64;");
        var bp = await CallAsync(client, "set_breakpoint", new Dictionary<string, object?>
        {
            ["file"] = TestEnvironment.MainActivitySource,
            ["line"] = nowLine,
        }, ct);
        var id = int.Parse(bp.Split(' ')[1]);

        var gone = await CallAsync(client, "remove_breakpoint", new Dictionary<string, object?> { ["id"] = id }, ct);
        Assert.Contains("Removed", gone, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No breakpoints", await CallAsync(client, "list_breakpoints", null, ct));

        await CallAsync(client, "launch_app", new Dictionary<string, object?>
        {
            ["deviceSerial"] = device.Serial,
            ["packageName"] = TestEnvironment.TestTargetPackage,
        }, ct);

        // Nothing is armed, so the app is running: pause is the only way to stop it.
        var paused = await CallAsync(client, "pause_execution", new Dictionary<string, object?> { ["timeoutSeconds"] = 20 }, ct);
        Assert.Contains("Stopped", paused, StringComparison.Ordinal);
        Assert.Contains("state=Stopped", await CallAsync(client, "get_debug_session_status", null, ct));

        await CallAsync(client, "terminate_app", null, ct);
    }

    /// <summary>
    /// The ways a session ends, each through the server. Detach and stop_debugging both terminate
    /// the app on Mono Android; what matters is that they answer and leave no session behind.
    /// </summary>
    [Fact]
    public async Task LifecycleTools_EndTheSession_HoweverItIsAskedFor()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await using var client = await ConnectAsync(cts.Token);
        var ct = cts.Token;

        // attach_to_app is launch_app without a deploy: on Mono Android attaching is a restart.
        var attached = await CallAsync(client, "attach_to_app", new Dictionary<string, object?>
        {
            ["deviceSerial"] = device.Serial,
            ["packageName"] = TestEnvironment.TestTargetPackage,
        }, ct);
        Assert.Contains("state=Running", attached);

        await CallAsync(client, "detach_debugger", null, ct);
        Assert.Contains("No session", await CallAsync(client, "get_debug_session_status", null, ct));

        await CallAsync(client, "launch_app", new Dictionary<string, object?>
        {
            ["deviceSerial"] = device.Serial,
            ["packageName"] = TestEnvironment.TestTargetPackage,
        }, ct);
        await CallAsync(client, "stop_debugging", null, ct);
        Assert.Contains("No session", await CallAsync(client, "get_debug_session_status", null, ct));
    }

    /// <summary>
    /// Fails when a tool is added without end-to-end coverage. The engine behind a tool can be
    /// thoroughly covered while the tool itself is not: a wrong parameter name, or a rendering
    /// that throws, only shows up when the tool is called through the server.
    /// </summary>
    [Fact]
    public async Task EveryTool_IsExercisedSomewhereInThisSuite()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await using var client = await ConnectAsync(cts.Token);
        var exposed = (await client.ListToolsAsync(cancellationToken: cts.Token)).Select(t => t.Name).ToHashSet();

        var source = File.ReadAllText(Path.Combine(TestEnvironment.RepoRoot, "tests", "NetAndroidDebugger.Tests", "McpEndToEndTests.cs"));
        var uncovered = exposed.Where(name => !source.Contains($"\"{name}\"", StringComparison.Ordinal)).OrderBy(n => n).ToList();

        Assert.True(uncovered.Count == 0,
            "these tools are never called through the server: " + string.Join(", ", uncovered));
    }

    /// <summary>
    /// The logpoint path through the server, because this is the form a real app usually needs:
    /// suspending an app that talks to a backend makes it time out, tracing does not.
    /// </summary>
    [Fact]
    public async Task Logpoint_TracesToDebuggerOutput_WithoutStoppingTheApp()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var client = await ConnectAsync(cts.Token);
        var ct = cts.Token;

        var tickLine = TestEnvironment.LineOf(TestEnvironment.MainActivitySource, "Android.Util.Log.Debug(\"TestTarget\", message);");
        var bp = await CallAsync(client, "set_breakpoint", new Dictionary<string, object?>
        {
            ["file"] = TestEnvironment.MainActivitySource,
            ["line"] = tickLine,
            ["logMessage"] = "tick {_ticks}",
        }, ct);
        Assert.StartsWith("bp 1 ", bp);

        await CallAsync(client, "launch_app", new Dictionary<string, object?>
        {
            ["deviceSerial"] = device.Serial,
            ["packageName"] = TestEnvironment.TestTargetPackage,
        }, ct);

        // Long enough for several ticks. A logpoint that suspended the app would leave the session
        // stopped instead of running.
        await Task.Delay(TimeSpan.FromSeconds(12), ct);
        var status = await CallAsync(client, "get_debug_session_status", null, ct);
        Assert.Contains("state=Running", status);

        var log = await CallAsync(client, "get_debugger_output", new Dictionary<string, object?> { ["maxLines"] = 500 }, ct);
        Assert.Contains("logpoint", log, StringComparison.Ordinal);
        Assert.DoesNotContain("{_ticks}", log, StringComparison.Ordinal);
        Assert.Matches(@"tick \d+", log);

        await CallAsync(client, "terminate_app", null, ct);
    }

    /// <summary>
    /// The rule engine through the server: rules are parsed, read back, and actually applied - a
    /// noisy exception is let through while the app keeps running.
    /// </summary>
    [Fact]
    public async Task ExceptionRules_ParsedAndApplied_ThroughTheServer()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var client = await ConnectAsync(cts.Token);
        var ct = cts.Token;

        var set = await CallAsync(client, "set_exception_rules", new Dictionary<string, object?>
        {
            ["rules"] = """
                [{"type":"System.InvalidOperationException","action":"ignore"},
                 {"action":"break"}]
                """,
        }, ct);
        Assert.Contains("ignore on type=System.InvalidOperationException", set);
        Assert.Contains("break on any exception", set);

        // Read back through the server, in order.
        var read = await CallAsync(client, "get_exception_rules", null, ct);
        Assert.StartsWith("1. ignore on type=System.InvalidOperationException", read);

        await CallAsync(client, "set_exception_filters", new Dictionary<string, object?>
        {
            ["firstChanceTypes"] = new[] { "System.InvalidOperationException" },
        }, ct);
        await CallAsync(client, "launch_app", new Dictionary<string, object?>
        {
            ["deviceSerial"] = device.Serial,
            ["packageName"] = TestEnvironment.TestTargetPackage,
        }, ct);

        // The app raises that exception every fifth tick. The rule lets it through, so after
        // several of them the session is still running.
        await Task.Delay(TimeSpan.FromSeconds(20), ct);
        Assert.Contains("state=Running", await CallAsync(client, "get_debug_session_status", null, ct));

        await CallAsync(client, "terminate_app", null, ct);
    }

    [Fact]
    public async Task ExceptionRules_BadInput_IsRejectedWithWhatWasExpected()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await using var client = await ConnectAsync(cts.Token);
        var ct = cts.Token;

        var notJson = await client.CallToolAsync("set_exception_rules",
            new Dictionary<string, object?> { ["rules"] = "{not json" }, cancellationToken: ct);
        Assert.True(notJson.IsError);

        var notAnArray = await client.CallToolAsync("set_exception_rules",
            new Dictionary<string, object?> { ["rules"] = """{"action":"ignore"}""" }, cancellationToken: ct);
        Assert.True(notAnArray.IsError);

        var badAction = await client.CallToolAsync("set_exception_rules",
            new Dictionary<string, object?> { ["rules"] = """[{"action":"explode"}]""" }, cancellationToken: ct);
        Assert.True(badAction.IsError);
        var text = string.Join(" ", badAction.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.Contains("break", text, StringComparison.Ordinal);

        // Still serving, and the rules were left alone.
        Assert.Contains("No exception rules", await CallAsync(client, "get_exception_rules", null, ct));
    }
}
