using System.Text.Json.Nodes;
using NetAndroidDebugger.Tests.Harness;
using Xunit.Abstractions;

namespace NetAndroidDebugger.Tests;

/// <summary>
/// Drives the real DAP adapter process over stdio, the way an editor would, against TestTarget.
/// </summary>
[Collection(DeviceCollection.Name)]
[Trait("Category", "Device")]
public sealed class DapEndToEndTests(DeviceFixture device, ITestOutputHelper output)
{
    private const string BuildConfiguration =
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    private static string AdapterDll =>
        Path.Combine(TestEnvironment.RepoRoot, "src", "NetAndroidDebugger.Dap", "bin", BuildConfiguration, "net10.0", "NetAndroidDebugger.Dap.dll");

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);

    private DapClient Start()
    {
        Assert.True(File.Exists(AdapterDll), $"DAP adapter not built: {AdapterDll}");
        return DapClient.Start(AdapterDll, output.WriteLine);
    }

    private JsonObject LaunchArgs() => new()
    {
        ["deviceSerial"] = device.Serial,
        ["packageName"] = TestEnvironment.TestTargetPackage,
    };

    [Fact]
    public async Task Initialize_ReportsTheCapabilitiesAClientNeeds()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await using var client = Start();

        var body = await client.ExpectAsync("initialize", new JsonObject { ["adapterID"] = "net-android-debugger" }, cts.Token);
        Assert.True(body["supportsConfigurationDoneRequest"]?.GetValue<bool>());
        Assert.True(body["supportsConditionalBreakpoints"]?.GetValue<bool>());
        Assert.True(body["supportsExceptionInfoRequest"]?.GetValue<bool>());

        // The client may only send configuration requests after this event.
        Assert.NotNull(await client.WaitForEventAsync("initialized", TimeSpan.FromSeconds(10), cts.Token));
    }

    [Fact]
    public async Task Roundtrip_Launch_Breakpoint_Stack_Scopes_Variables_Evaluate_Continue_Disconnect()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;
        await using var client = Start();

        await client.ExpectAsync("initialize", new JsonObject { ["adapterID"] = "net-android-debugger" }, ct);
        Assert.NotNull(await client.WaitForEventAsync("initialized", TimeSpan.FromSeconds(10), ct));

        // Breakpoints are configured before launch, exactly as an editor does it.
        // The line other tests break on: by then the tick's locals are assigned.
        var tickLine = TestEnvironment.LineOf(TestEnvironment.MainActivitySource, "Android.Util.Log.Debug(\"TestTarget\", message);");
        var breakpoints = await client.ExpectAsync("setBreakpoints", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = TestEnvironment.MainActivitySource },
            ["breakpoints"] = new JsonArray(new JsonObject { ["line"] = tickLine }),
        }, ct);
        Assert.Single(breakpoints["breakpoints"]!.AsArray());

        await client.ExpectAsync("launch", LaunchArgs(), ct);
        await client.ExpectAsync("configurationDone", null, ct);

        var stopped = await client.WaitForEventAsync("stopped", StopTimeout, ct);
        Assert.NotNull(stopped);
        var stopBody = stopped["body"]!.AsObject();
        Assert.Equal("breakpoint", stopBody["reason"]?.GetValue<string>());
        var threadId = stopBody["threadId"]!.GetValue<int>();

        // Threads name their process: one DAP thread list covers every process of the app.
        var threads = (await client.ExpectAsync("threads", null, ct))["threads"]!.AsArray();
        Assert.NotEmpty(threads);
        Assert.All(threads, t => Assert.Contains("pid ", t!["name"]!.GetValue<string>()));

        var frames = (await client.ExpectAsync("stackTrace", new JsonObject { ["threadId"] = threadId }, ct))["stackFrames"]!.AsArray();
        Assert.NotEmpty(frames);
        var top = frames[0]!.AsObject();
        Assert.Equal(tickLine, top["line"]!.GetValue<int>());
        Assert.Equal(TestEnvironment.MainActivitySource, top["source"]!["path"]!.GetValue<string>(), ignoreCase: true);
        var frameId = top["id"]!.GetValue<int>();

        var scopes = (await client.ExpectAsync("scopes", new JsonObject { ["frameId"] = frameId }, ct))["scopes"]!.AsArray();
        var localsRef = scopes[0]!["variablesReference"]!.GetValue<int>();
        Assert.True(localsRef > 0);

        var locals = (await client.ExpectAsync("variables", new JsonObject { ["variablesReference"] = localsRef }, ct))["variables"]!.AsArray();
        var sample = locals.Single(v => v!["name"]!.GetValue<string>() == "sample").AsObject();
        var sampleRef = sample["variablesReference"]!.GetValue<int>();
        Assert.True(sampleRef > 0, "an object local must be expandable");

        var members = (await client.ExpectAsync("variables", new JsonObject { ["variablesReference"] = sampleRef }, ct))["variables"]!.AsArray();
        Assert.Contains(members, m => m!["name"]!.GetValue<string>() == "Name");

        var evaluated = await client.ExpectAsync("evaluate", new JsonObject
        {
            ["expression"] = "1 + 1",
            ["frameId"] = frameId,
            ["context"] = "repl",
        }, ct);
        Assert.Equal("2", evaluated["result"]!.GetValue<string>());

        // Continue must be acknowledged and must actually resume the app.
        var continued = await client.ExpectAsync("continue", new JsonObject { ["threadId"] = threadId }, ct);
        Assert.True(continued["allThreadsContinued"]?.GetValue<bool>() ?? true);
        Assert.NotNull(await client.WaitForEventAsync("stopped", StopTimeout, ct));

        await client.ExpectAsync("disconnect", new JsonObject { ["terminateDebuggee"] = true }, ct);
    }

    [Fact]
    public async Task Stepping_MovesToTheNextLine_AndReportsAStepStop()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;
        await using var client = Start();

        await client.ExpectAsync("initialize", new JsonObject { ["adapterID"] = "net-android-debugger" }, ct);
        Assert.NotNull(await client.WaitForEventAsync("initialized", TimeSpan.FromSeconds(10), ct));

        var stepFrom = TestEnvironment.LineOf(TestEnvironment.MainActivitySource, "long now = Environment.TickCount64;");
        await client.ExpectAsync("setBreakpoints", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = TestEnvironment.MainActivitySource },
            ["breakpoints"] = new JsonArray(new JsonObject { ["line"] = stepFrom }),
        }, ct);
        await client.ExpectAsync("launch", LaunchArgs(), ct);
        await client.ExpectAsync("configurationDone", null, ct);

        var stopped = await client.WaitForEventAsync("stopped", StopTimeout, ct);
        Assert.NotNull(stopped);
        var threadId = stopped["body"]!["threadId"]!.GetValue<int>();

        // Clear the breakpoint first: it is on a line the app runs every second, and a fresh hit
        // during the step would be the stop the client sees.
        await client.ExpectAsync("setBreakpoints", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = TestEnvironment.MainActivitySource },
            ["breakpoints"] = new JsonArray(),
        }, ct);

        await client.ExpectAsync("next", new JsonObject { ["threadId"] = threadId }, ct);
        var afterStep = await client.WaitForEventAsync("stopped", StopTimeout, ct);
        Assert.NotNull(afterStep);
        Assert.Equal("step", afterStep["body"]!["reason"]!.GetValue<string>());

        var frames = (await client.ExpectAsync("stackTrace", new JsonObject { ["threadId"] = threadId }, ct))["stackFrames"]!.AsArray();
        Assert.Equal(stepFrom + 1, frames[0]!["line"]!.GetValue<int>());

        await client.ExpectAsync("disconnect", new JsonObject { ["terminateDebuggee"] = true }, ct);
    }



    [Fact]
    public async Task Events_ArriveInTheOrderTheyHappened()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;
        await using var client = Start();

        await client.ExpectAsync("initialize", new JsonObject { ["adapterID"] = "net-android-debugger" }, ct);
        Assert.NotNull(await client.WaitForEventAsync("initialized", TimeSpan.FromSeconds(10), ct));

        var tickLine = TestEnvironment.LineOf(TestEnvironment.MainActivitySource, "Android.Util.Log.Debug(\"TestTarget\", message);");
        await client.ExpectAsync("setBreakpoints", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = TestEnvironment.MainActivitySource },
            ["breakpoints"] = new JsonArray(new JsonObject { ["line"] = tickLine }),
        }, ct);
        await client.ExpectAsync("launch", LaunchArgs(), ct);
        await client.ExpectAsync("configurationDone", null, ct);

        // Continue several times over a breakpoint the app hits every second, with app output
        // flowing the whole while. A client reads events as a sequence: a `continued` delivered
        // after the `stopped` that followed it would leave it showing the wrong state.
        var stopped = await client.WaitForEventAsync("stopped", StopTimeout, ct);
        Assert.NotNull(stopped);
        var threadId = stopped["body"]!["threadId"]!.GetValue<int>();

        for (var i = 0; i < 3; i++)
        {
            await client.ExpectAsync("continue", new JsonObject { ["threadId"] = threadId }, ct);
            Assert.NotNull(await client.WaitForEventAsync("stopped", StopTimeout, ct));
        }

        var order = client.EventLog
            .Where(e => e is "stopped" or "continued")
            .ToList();
        // Every stop is preceded by the resume that led to it, and no two of either run together.
        for (var i = 1; i < order.Count; i++)
            Assert.True(order[i] != order[i - 1], $"two '{order[i]}' events in a row: {string.Join(" -> ", order)}");

        await client.ExpectAsync("disconnect", new JsonObject { ["terminateDebuggee"] = true }, ct);
    }
    [Fact]
    public async Task MalformedInput_IsIgnored_AndTheAdapterKeepsServing()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var ct = cts.Token;
        await using var client = Start();

        await client.ExpectAsync("initialize", new JsonObject { ["adapterID"] = "net-android-debugger" }, ct);

        // A wrong Content-Length is the classic client bug: the body it announces does not match
        // what it wrote. The adapter must not die on it — there is nothing to answer (no seq is
        // parseable), but the next well-formed request has to work.
        await client.SendRawAsync("Content-Length: 12\r\n\r\n{\"seq\":1,\"ty", ct);

        var threads = await client.SendAsync("threads", null, ct);
        Assert.True(threads["success"]!.GetValue<bool>());
    }
    [Fact]
    public async Task ErrorPaths_AreAnswered_NeverLeaveTheClientWaiting()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        await using var client = Start();

        await client.ExpectAsync("initialize", new JsonObject { ["adapterID"] = "net-android-debugger" }, ct);

        // Unknown request, missing arguments, and ids that address nothing: all must come back as
        // failed responses rather than silence.
        var unknown = await client.SendAsync("thisIsNotARequest", null, ct);
        Assert.False(unknown["success"]!.GetValue<bool>());

        var noPackage = await client.SendAsync("launch", new JsonObject { ["deviceSerial"] = device.Serial }, ct);
        Assert.False(noPackage["success"]!.GetValue<bool>());
        Assert.Contains("packageName", noPackage["message"]!.GetValue<string>());

        var staleFrame = await client.SendAsync("scopes", new JsonObject { ["frameId"] = 987654 }, ct);
        Assert.False(staleFrame["success"]!.GetValue<bool>());

        var staleVariable = await client.SendAsync("variables", new JsonObject { ["variablesReference"] = 987654 }, ct);
        Assert.False(staleVariable["success"]!.GetValue<bool>());

        // Nothing is stopped, so there is no frame to evaluate in — and the adapter says so.
        var evaluate = await client.SendAsync("evaluate", new JsonObject { ["expression"] = "1 + 1" }, ct);
        Assert.False(evaluate["success"]!.GetValue<bool>());

        // And it is still serving requests afterwards.
        var threads = await client.SendAsync("threads", null, ct);
        Assert.True(threads["success"]!.GetValue<bool>());
    }

    /// <summary>
    /// A client sends the enabled exception filters two ways, and once any filter advertises
    /// `supportsCondition` it uses the second: `filterOptions`, with `filters` left EMPTY. Reading
    /// only the legacy array makes every first-chance filter a silent no-op — the Delphi debugger
    /// records finding exactly that under real VS Code, with its own test masking it by populating
    /// both. This drives the real shape.
    /// </summary>
    [Fact]
    public async Task ExceptionFilters_ArriveAsFilterOptions_AndSelectTheTypes()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;
        await using var client = Start();

        var caps = await client.ExpectAsync("initialize", new JsonObject { ["adapterID"] = "net-android-debugger" }, ct);
        Assert.True(caps["supportsExceptionFilterOptions"]?.GetValue<bool>());
        var filters = caps["exceptionBreakpointFilters"]!.AsArray();
        var types = filters.Single(f => f!["filter"]!.GetValue<string>() == "types");
        Assert.True(types["supportsCondition"]?.GetValue<bool>(), "the type filter must accept a condition");
        Assert.NotNull(await client.WaitForEventAsync("initialized", TimeSpan.FromSeconds(10), ct));

        // The shape a real client sends: `filters` empty, ids under `filterOptions.filterId`.
        await client.ExpectAsync("setExceptionBreakpoints", new JsonObject
        {
            ["filters"] = new JsonArray(),
            ["filterOptions"] = new JsonArray(new JsonObject
            {
                ["filterId"] = "types",
                ["condition"] = "System.InvalidOperationException",
            }),
        }, ct);

        await client.ExpectAsync("launch", LaunchArgs(), ct);
        await client.ExpectAsync("configurationDone", null, ct);

        // TestTarget throws a caught InvalidOperationException every fifth tick. Nothing else is
        // armed, so a stop can only be that exception.
        var stopped = await client.WaitForEventAsync("stopped", StopTimeout, ct);
        Assert.NotNull(stopped);
        Assert.Equal("exception", stopped["body"]!["reason"]!.GetValue<string>());

        var info = await client.ExpectAsync("exceptionInfo", new JsonObject
        {
            ["threadId"] = stopped["body"]!["threadId"]!.GetValue<int>(),
        }, ct);
        Assert.Contains("InvalidOperationException", info["exceptionId"]!.GetValue<string>(), StringComparison.Ordinal);

        await client.ExpectAsync("disconnect", new JsonObject { ["terminateDebuggee"] = true }, ct);
    }

    /// <summary>
    /// The legacy shape still works: a client that sends only `filters` must not end up with the
    /// filters silently off.
    /// </summary>
    [Fact]
    public async Task ExceptionFilters_LegacyFiltersArray_StillSelectsAll()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;
        await using var client = Start();

        await client.ExpectAsync("initialize", new JsonObject { ["adapterID"] = "net-android-debugger" }, ct);
        Assert.NotNull(await client.WaitForEventAsync("initialized", TimeSpan.FromSeconds(10), ct));

        await client.ExpectAsync("setExceptionBreakpoints", new JsonObject
        {
            ["filters"] = new JsonArray("all"),
        }, ct);

        await client.ExpectAsync("launch", LaunchArgs(), ct);
        await client.ExpectAsync("configurationDone", null, ct);

        var stopped = await client.WaitForEventAsync("stopped", StopTimeout, ct);
        Assert.NotNull(stopped);
        Assert.Equal("exception", stopped["body"]!["reason"]!.GetValue<string>());

        await client.ExpectAsync("disconnect", new JsonObject { ["terminateDebuggee"] = true }, ct);
    }
}
