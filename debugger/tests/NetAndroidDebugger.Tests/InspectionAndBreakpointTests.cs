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
        await using var session = await LaunchAsync(cts.Token, s => s.SetBreakpoint(new BreakpointSpec(Main, TickLine, Condition: "_ticks % 7 == 3")));

        var stop = await session.WaitForStopAsync(0, TimeSpan.FromSeconds(30), cts.Token);
        Assert.NotNull(stop);
        // A recurring condition on purpose: `_ticks == 3` is only ever true in the app's first
        // seconds, so a slow attach turns the test into a guaranteed failure instead of a check.
        Assert.Equal(3, int.Parse(Eval(session, stop, "_ticks")) % 7);
    }

    [Fact]
    public async Task HitCountBreakpoint_StopsAtNthHit()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var session = await LaunchAsync(cts.Token, s => s.SetBreakpoint(new BreakpointSpec(Main, TickLine, HitCount: 3)));

        var stop = await session.WaitForStopAsync(0, TimeSpan.FromSeconds(30), cts.Token);
        Assert.NotNull(stop);
        // At least the requested number of hits must have gone by. It can be more: the breakpoint
        // store is shared by every process of the app, and a second process attaching re-registers
        // the breakpoint, which appears to restart Mono's hit counter (KNOWN_UNKNOWNS U13).
        var ticks = int.Parse(Eval(session, stop, "_ticks"));
        output.WriteLine($"hit-count breakpoint (3) stopped at tick {ticks}");
        Assert.True(ticks >= 3, $"stopped too early, at tick {ticks}");
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

    /// <summary>
    /// Binding is asynchronous, so the value <see cref="DebugSession.SetBreakpoint"/> returns the
    /// instant it is called says "not bound" with a status message that reads like a failure, even
    /// for a type that is about to load. The settle window in
    /// <see cref="DebugSession.SetBreakpointAsync"/> is what the frontends report.
    /// </summary>
    [Fact]
    public async Task SetBreakpointAsync_WaitsForTheRuntimeToBindIt()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var session = await LaunchAsync(cts.Token);
        Assert.Equal(SessionState.Running, session.State);

        var bp = await session.SetBreakpointAsync(new BreakpointSpec(Main, TickLine), TimeSpan.FromSeconds(5), cts.Token);
        Assert.True(bp.Verified, $"reported unbound: {bp.Message ?? "no message"}");

        // Before any process is attached nothing can bind a breakpoint, so the call must not wait.
        await using var idle = new DebugSession();
        var started = DateTime.UtcNow;
        var pending = await idle.SetBreakpointAsync(new BreakpointSpec(Main, TickLine), TimeSpan.FromSeconds(5), cts.Token);
        Assert.False(pending.Verified);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
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

    [Fact]
    public async Task StopLocation_IsAlwaysTheUserLine_EvenWhenStoppedInsideAnExternalCall()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        // TickLine calls Android.Util.Log.Debug: the stop event is sometimes delivered with the
        // JNI callee on top of the stack, which used to be reported as the stop location.
        await using var session = await LaunchAsync(cts.Token, s => s.SetBreakpoint(new BreakpointSpec(Main, TickLine)));

        var stop = await session.WaitForStopAsync(0, StopTimeout, cts.Token);
        for (int i = 0; i < 6; i++)
        {
            Assert.NotNull(stop);
            output.WriteLine($"stop {i}: {stop.Location?.Method} {stop.Location?.File}:{stop.Location?.Line}");
            Assert.EndsWith("MainActivity.cs", stop.Location?.File);
            Assert.Equal(TickLine, stop.Location?.Line);
            Assert.Contains("Tick", stop.Location?.Method);
            stop = await session.ContinueAndWaitAsync(StopTimeout, cts.Token);
        }
    }

    [Fact]
    public async Task RepeatedExpansion_WithAFrequentBreakpointArmed_KeepsWorking()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        // TickLine is hit every second on a pool thread. Mono resumes all threads during a
        // debuggee invocation, so without disarming breakpoints first, another thread hits this
        // breakpoint mid-invocation, suspends the VM, and the invocation never returns.
        await using var session = await LaunchAsync(cts.Token, s => s.SetBreakpoint(new BreakpointSpec(Main, TickLine)));

        var stop = await session.WaitForStopAsync(0, StopTimeout, cts.Token);
        Assert.NotNull(stop);
        for (int round = 0; round < 5; round++)
        {
            var sample = session.GetLocals(stop.Pid, stop.ThreadId).Single(l => l.Name == "sample");
            var children = session.ExpandVariable(sample.ExpansionHandle!);
            Assert.Contains(children, c => c.Name == "When");
            Assert.Contains(children, c => c.Name == "NumbersCount" && !c.IsError);
            output.WriteLine($"round {round}: expanded {children.Count} members");
        }
        // The breakpoints are armed again afterwards, so execution still stops.
        Assert.Single(session.ListBreakpoints());
        Assert.NotNull(await session.ContinueAndWaitAsync(StopTimeout, cts.Token));
    }

    [Fact]
    public async Task AbortedSlowInvoke_LeavesTheThreadUsable()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var probeLine = TestEnvironment.LineOf(Main, "Android.Util.Log.Verbose(\"TestTarget\", $\"probe {fast}\");");
        await using var session = await LaunchAsync(cts.Token, s =>
        {
            // Short timeouts so SlowProbe.SlowValue (8 s getter) is certainly aborted.
            s.SetEvaluationOptions(evaluationTimeoutMs: 1500, memberEvaluationTimeoutMs: 1500);
            s.SetBreakpoint(new BreakpointSpec(Main, probeLine));
        });

        var stop = await session.WaitForStopAsync(0, StopTimeout, cts.Token);
        Assert.NotNull(stop);
        Assert.Equal("7", Eval(session, stop, "slow.FastValue"));

        var timedOut = session.Evaluate(stop.Pid, stop.ThreadId, 0, "slow.SlowValue");
        Assert.True(timedOut.IsError);

        // The timeout is reported as an error and the debugger stays responsive: further
        // evaluation and expansion answer promptly, the slow member reading as an error.
        Assert.Equal("7", Eval(session, stop, "slow.FastValue"));
        var probe = session.GetLocals(stop.Pid, stop.ThreadId).Single(l => l.Name == "slow");
        var children = session.ExpandVariable(probe.ExpansionHandle!).ToDictionary(c => c.Name);
        Assert.Equal("7", children["FastValue"].Value);
        Assert.True(children["SlowValue"].IsError);

        // What the debuggee does afterwards is NOT guaranteed: an invocation stuck in a
        // non-interruptible call (Thread.Sleep here) can survive the abort, or the runtime can
        // tear the process down while retrying it. Both are acceptable; a hung or inconsistent
        // debugger is not (KNOWN_UNKNOWNS U11).
        var next = await session.ContinueAndWaitAsync(StopTimeout, cts.Token);
        output.WriteLine($"after continue: stop={next?.Reason.ToString() ?? "none"} state={session.State}");
        Assert.True(next is not null || session.State is SessionState.Running or SessionState.Exited,
            $"session left in an unusable state: {session.State}");
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
        // Tick fires every second on another pool thread: drop the breakpoint so a slow step
        // cannot be overtaken by the next hit.
        session.RemoveAllBreakpoints();
        var inside = await session.StepIntoAsync(stop.Pid, stop.ThreadId, StopTimeout, cts.Token);
        Assert.NotNull(inside);
        Assert.Contains("Describe", inside.Location?.Method);
        Assert.NotEqual(callLine, inside.Location?.Line);

        var back = await session.StepOutAsync(stop.Pid, stop.ThreadId, StopTimeout, cts.Token);
        Assert.NotNull(back);
        Assert.Contains("Tick", back.Location?.Method);
        Assert.Equal(callLine, back.Location?.Line);
    }

    [Fact]
    public async Task AsyncFrame_StopsAfterAwait_WithLocalsAndUserStack()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        // The line after `await Task.Delay(...)`: the continuation runs on another thread,
        // in the compiler-generated state machine.
        var afterAwait = TestEnvironment.LineOf(Main, "int after = before + 1;");
        await using var session = await LaunchAsync(cts.Token, s => s.SetBreakpoint(new BreakpointSpec(Main, afterAwait)));

        var stop = await session.WaitForStopAsync(0, StopTimeout, cts.Token);
        Assert.NotNull(stop);
        Assert.Equal(afterAwait, stop.Location?.Line);
        Assert.Contains("AsyncProbeAsync", stop.Location?.Method);

        // Locals defined before the await survive into the continuation.
        var locals = session.GetLocals(stop.Pid, stop.ThreadId);
        output.WriteLine(string.Join("\n", locals.Select(l => $"{l.Name} : {l.TypeName} = {l.DisplayValue}")));
        var before = locals.Single(l => l.Name == "before");
        Assert.Equal("int", before.TypeName);
        Assert.True(int.TryParse(before.Value, out _), $"before = {before.Value}");

        // The stack of an async continuation is mostly runtime plumbing; the user frame must be there.
        var frames = session.GetCallStack(stop.Pid, stop.ThreadId);
        Assert.Contains(frames, f => f.Method.Contains("AsyncProbeAsync") && !f.IsExternal);

        // Stepping over the line after the await stays inside the method.
        var stepped = await session.StepOverAsync(stop.Pid, stop.ThreadId, StopTimeout, cts.Token);
        Assert.NotNull(stepped);
        Assert.Contains("AsyncProbeAsync", stepped.Location?.Method);
        Assert.True(stepped.Location?.Line > afterAwait, $"stepped to line {stepped.Location?.Line}");
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
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var session = await LaunchAsync(cts.Token, s =>
        {
            // Expanding this object invokes every getter, and the first invocation in a process
            // pays for runtime initialisation (ICU behind DateTime formatting). On a slow device
            // the default timeout can expire there, and an aborted invocation is worse than a
            // slow one, so give this test room.
            s.SetEvaluationOptions(evaluationTimeoutMs: 30000, memberEvaluationTimeoutMs: 40000);
            s.SetBreakpoint(new BreakpointSpec(Main, TickLine));
        });

        var stop = await session.WaitForStopAsync(0, StopTimeout, cts.Token);
        Assert.NotNull(stop);
        var sample = session.GetLocals(stop.Pid, stop.ThreadId).Single(l => l.Name == "sample");
        Assert.True(sample.HasChildren);
        Assert.NotNull(sample.ExpansionHandle);

        var children = session.ExpandVariable(sample.ExpansionHandle!);
        var byName = children.ToDictionary(c => c.Name);
        output.WriteLine(string.Join("\n", children.Select(c => $"{c.Name} : {c.TypeName} = {c.DisplayValue}")));

        // Which tick we stop on depends on timing; assert shape, not the tick number.
        var tick = long.Parse(byName["Tick"].Value);
        Assert.True(tick >= 1);
        Assert.Equal($"\"sample-{tick}\"", byName["Name"].Value);
        // Mono renders the enum value type-qualified in Value; DisplayValue is the bare member.
        var expectedKind = tick % 2 == 0 ? "Even" : "Odd";
        Assert.Equal(expectedKind, byName["Kind"].DisplayValue);
        Assert.Contains(expectedKind, byName["Kind"].Value);
        Assert.Contains("0.5", byName["Ratio"].Value);
        Assert.Contains("3", byName["NumbersCount"].Value);

        // Generic types are rendered with their type arguments, not as raw `List\`1`.
        Assert.Contains("List<int>", byName["Numbers"].TypeName.Replace(" ", ""));
        Assert.Contains("Dictionary<string,int>", byName["Map"].TypeName.Replace(" ", ""));

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
        Assert.StartsWith("\"sample-", Eval(session, stop, "sample.Name"));
    }
}
