using NetAndroidDebugger.Core;
using NetAndroidDebugger.Tests.Harness;
using Xunit.Abstractions;

namespace NetAndroidDebugger.Tests;

/// <summary>Breakpoint variants, execution control and value inspection against TestTarget.</summary>
[Collection(DeviceCollection.Name)]
public sealed class InspectionAndBreakpointTests(DeviceFixture device, ITestOutputHelper output)
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(25);
    private static readonly string TickMarker = "Android.Util.Log.Debug(\"TestTarget\", message);";
    private static readonly string DescribeCallMarker = "string described = Describe(sample);";
    private static readonly string OnCreateMarker = "SetContentView(Resource.Layout.activity_main);";

    private static string Main => TestEnvironment.MainActivitySource;
    private int TickLine => TestEnvironment.LineOf(Main, TickMarker);

    private async Task<DebugSession> LaunchAsync(CancellationToken ct, Action<DebugSession>? beforeLaunch = null)
    {
        var session = device.NewSession(output.WriteLine);
        beforeLaunch?.Invoke(session);
        await session.LaunchAsync(TestEnvironment.TestTargetApp(), device.Options(), ct);
        return session;
    }

    private static string Eval(DebugSession s, StopEvent stop, string expr) => s.Evaluate(stop.Pid, stop.ThreadId, 0, expr).Value;

    // ------------------------------------------------------------------ threads

    [Fact]
    public async Task MainThread_IsLabelledMain_AndOnCreateRunsOnIt()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var line = TestEnvironment.LineOf(Main, OnCreateMarker);
        await using var session = await LaunchAsync(cts.Token, s => s.SetBreakpoint(new BreakpointSpec(Main, line)));

        var stop = await session.WaitForStopAsync(0, StopTimeout, cts.Token);
        Assert.NotNull(stop);
        Assert.Equal(1, stop.ThreadId);
        var main = session.GetThreads(stop.Pid).Single(t => t.Id == stop.ThreadId);
        Assert.Equal("Main", main.Name);
        Assert.DoesNotContain(session.GetThreads(stop.Pid), t => string.IsNullOrEmpty(t.Name));
    }

    // ------------------------------------------------------------------ breakpoint variants

    [Fact]
    public async Task ConditionalBreakpoint_StopsOnlyWhenConditionIsTrue()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var session = await LaunchAsync(cts.Token, s => s.SetBreakpoint(new BreakpointSpec(Main, TickLine, Condition: "_ticks == 3")));

        var stop = await session.WaitForStopAsync(0, TimeSpan.FromSeconds(30), cts.Token);
        Assert.NotNull(stop);
        Assert.Equal("3", Eval(session, stop, "_ticks"));
    }

    [Fact]
    public async Task HitCountBreakpoint_StopsAtNthHit()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var session = await LaunchAsync(cts.Token, s => s.SetBreakpoint(new BreakpointSpec(Main, TickLine, HitCount: 3)));

        var stop = await session.WaitForStopAsync(0, TimeSpan.FromSeconds(30), cts.Token);
        Assert.NotNull(stop);
        Assert.Equal("3", Eval(session, stop, "_ticks"));
    }

    [Fact]
    public async Task SetBreakpoint_WhileRunning_IsBoundAndHit()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var session = await LaunchAsync(cts.Token);
        Assert.Equal(SessionState.Running, session.State);

        var bp = session.SetBreakpoint(new BreakpointSpec(Main, TickLine));
        var stop = await session.WaitForStopAsync(0, StopTimeout, cts.Token);
        Assert.NotNull(stop);
        Assert.Equal(TickLine, stop.Location?.Line);
        Assert.True(session.ListBreakpoints().Single(b => b.Id == bp.Id).Verified);
    }

    [Fact]
    public async Task RemoveAllBreakpoints_WhileStopped_NoFurtherHits()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var session = await LaunchAsync(cts.Token, s => s.SetBreakpoint(new BreakpointSpec(Main, TickLine)));

        Assert.NotNull(await session.WaitForStopAsync(0, StopTimeout, cts.Token));
        session.RemoveAllBreakpoints();
        Assert.Empty(session.ListBreakpoints());
        var next = await session.ContinueAndWaitAsync(TimeSpan.FromSeconds(3), cts.Token);
        Assert.Null(next);
        Assert.Equal(SessionState.Running, session.State);
    }

    [Fact]
    public async Task Pause_StopsRunningProcess_AndReportsPauseReason()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var session = await LaunchAsync(cts.Token);

        var stop = await session.PauseAsync(TimeSpan.FromSeconds(10), cts.Token);
        Assert.NotNull(stop);
        Assert.Equal(StopReason.Pause, stop.Reason);
        Assert.Equal(SessionState.Stopped, session.State);
        Assert.NotEmpty(session.GetThreads(stop.Pid));
    }

    // ------------------------------------------------------------------ stepping

    [Fact]
    public async Task StepInto_EntersCallee_AndStepOut_ReturnsToCaller()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var callLine = TestEnvironment.LineOf(Main, DescribeCallMarker);
        await using var session = await LaunchAsync(cts.Token, s => s.SetBreakpoint(new BreakpointSpec(Main, callLine)));

        var stop = await session.WaitForStopAsync(0, StopTimeout, cts.Token);
        Assert.NotNull(stop);
        var inside = await session.StepIntoAsync(stop.Pid, stop.ThreadId, StopTimeout, cts.Token);
        Assert.NotNull(inside);
        Assert.Contains("Describe", inside.Location?.Method);
        Assert.NotEqual(callLine, inside.Location?.Line);

        var back = await session.StepOutAsync(stop.Pid, stop.ThreadId, StopTimeout, cts.Token);
        Assert.NotNull(back);
        Assert.Contains("Tick", back.Location?.Method);
        Assert.Equal(callLine, back.Location?.Line);
    }

    // ------------------------------------------------------------------ exceptions

    [Fact]
    public async Task FirstChanceExceptionFilter_StopsOnThrow_WithDetails()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var session = await LaunchAsync(cts.Token, s => s.SetExceptionFilters(new ExceptionFilters(true, ["System.InvalidOperationException"])));

        var stop = await session.WaitForStopAsync(0, TimeSpan.FromSeconds(40), cts.Token);
        Assert.NotNull(stop);
        Assert.Equal(StopReason.Exception, stop.Reason);
        Assert.Contains("InvalidOperationException", stop.ExceptionType);

        // The message may need a debuggee call; it is resolved on demand by GetExceptionDetails.
        var details = session.GetExceptionDetails();
        Assert.NotNull(details);
        Assert.Contains("InvalidOperationException", details.Value.Type);
        Assert.Contains("expected failure", details.Value.Message);
    }

    // ------------------------------------------------------------------ values

    [Fact]
    public async Task ObjectExpansion_ShowsProperties_Enum_List_Array_Nested()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var session = await LaunchAsync(cts.Token, s => s.SetBreakpoint(new BreakpointSpec(Main, TickLine)));

        var stop = await session.WaitForStopAsync(0, StopTimeout, cts.Token);
        Assert.NotNull(stop);
        var sample = session.GetLocals(stop.Pid, stop.ThreadId).Single(l => l.Name == "sample");
        Assert.True(sample.HasChildren);
        Assert.NotNull(sample.ExpansionHandle);

        var children = session.ExpandVariable(sample.ExpansionHandle!);
        var byName = children.ToDictionary(c => c.Name);
        output.WriteLine(string.Join("\n", children.Select(c => $"{c.Name} : {c.TypeName} = {c.DisplayValue}")));

        Assert.Equal("1", byName["Tick"].Value);
        Assert.Equal("\"sample-1\"", byName["Name"].Value);
        // Mono renders the enum value type-qualified in Value; DisplayValue is the bare member.
        Assert.Equal("Odd", byName["Kind"].DisplayValue);
        Assert.Contains("Odd", byName["Kind"].Value);
        Assert.Contains("0.5", byName["Ratio"].Value);
        Assert.Contains("3", byName["NumbersCount"].Value);

        var numbers = byName["Numbers"];
        Assert.True(numbers.HasChildren);
        var items = session.ExpandVariable(numbers.ExpansionHandle!);
        Assert.Contains(items, i => i.Value == "1");
        Assert.Contains(items, i => i.Value == "3");

        var words = byName["Words"];
        Assert.True(words.HasChildren);
        Assert.Contains(session.ExpandVariable(words.ExpansionHandle!), i => i.Value == "\"beta\"");

        var inner = byName["Inner"];
        Assert.True(inner.HasChildren);
        var innerChildren = session.ExpandVariable(inner.ExpansionHandle!).ToDictionary(c => c.Name);
        Assert.Equal("0", innerChildren["Tick"].Value);
        Assert.Null(innerChildren["Inner"].ExpansionHandle);
    }

    [Fact]
    public async Task Evaluate_InvalidExpression_IsErrorNotCrash()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var session = await LaunchAsync(cts.Token, s => s.SetBreakpoint(new BreakpointSpec(Main, TickLine)));

        var stop = await session.WaitForStopAsync(0, StopTimeout, cts.Token);
        Assert.NotNull(stop);
        // A member that does not exist on a real object, and a syntactically broken expression:
        // both must come back as clean errors, not exceptions.
        Assert.True(session.Evaluate(stop.Pid, stop.ThreadId, 0, "sample.NoSuchMember").IsError);
        Assert.True(session.Evaluate(stop.Pid, stop.ThreadId, 0, "1 +").IsError);
        // The session is still usable afterwards.
        Assert.Equal("2", Eval(session, stop, "1 + 1"));
        Assert.Contains("sample-1", Eval(session, stop, "sample.Name"));
    }
}
