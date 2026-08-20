using NetAndroidDebugger.Core;
using NetAndroidDebugger.Tests.Harness;
using Xunit.Abstractions;

namespace NetAndroidDebugger.Tests;

/// <summary>Launch → attach → breakpoint → inspection, against TestTarget on the selected device.</summary>
[Collection(DeviceCollection.Name)]
public sealed class LaunchAndBreakpointTests(DeviceFixture device, ITestOutputHelper output)
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(20);
    private static readonly string TickMarker = "Android.Util.Log.Debug(\"TestTarget\", message);";

    private int TickLine => TestEnvironment.LineOf(TestEnvironment.MainActivitySource, TickMarker);
    private int HelperTickLine => TestEnvironment.LineOf(TestEnvironment.HelperServiceSource, TickMarker);

    private async Task<DebugSession> LaunchAsync(CancellationToken ct, Action<DebugSession>? beforeLaunch = null)
    {
        var session = device.NewSession(output.WriteLine);
        beforeLaunch?.Invoke(session);
        await session.LaunchAsync(TestEnvironment.TestTargetApp(), device.Options(), ct);
        return session;
    }

    [Fact]
    public async Task Launch_AttachesMainProcess_AndReportsRunning()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var session = await LaunchAsync(cts.Token);

        var status = session.GetStatus();
        Assert.Equal(SessionState.Running, status.State);
        Assert.Equal(device.Serial, status.DeviceSerial);
        Assert.Contains(status.Processes, p => p.Name == TestEnvironment.TestTargetPackage && !p.HasExited);
    }

    [Fact]
    public async Task Breakpoint_InMainProcess_IsHit_WithLocals()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var session = await LaunchAsync(cts.Token, s => s.SetBreakpoint(new BreakpointSpec(TestEnvironment.MainActivitySource, TickLine)));

        var stop = await session.WaitForStopAsync(0, StopTimeout, cts.Token);
        Assert.NotNull(stop);
        Assert.Equal(StopReason.Breakpoint, stop.Reason);
        Assert.Equal(TickLine, stop.Location?.Line);
        Assert.EndsWith("MainActivity.cs", stop.Location?.File);
        Assert.Equal(SessionState.Stopped, session.State);

        var locals = session.GetLocals(stop.Pid, stop.ThreadId);
        var names = locals.Select(l => l.Name).ToList();
        Assert.Contains("now", names);
        Assert.Contains("message", names);
        var message = locals.Single(l => l.Name == "message");
        Assert.Equal("string", message.TypeName);
        Assert.StartsWith("\"tick ", message.Value);

        var bps = session.ListBreakpoints();
        Assert.Single(bps);
        Assert.True(bps[0].Verified);
    }

    [Fact]
    public async Task Breakpoint_InHelperProcess_IsHit_ViaPortRotation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var session = await LaunchAsync(cts.Token, s => s.SetBreakpoint(new BreakpointSpec(TestEnvironment.HelperServiceSource, HelperTickLine)));

        var stop = await session.WaitForStopAsync(0, StopTimeout, cts.Token);
        Assert.NotNull(stop);
        Assert.Equal(StopReason.Breakpoint, stop.Reason);
        Assert.EndsWith("HelperService.cs", stop.Location?.File);

        var procs = session.GetProcesses();
        var helper = Assert.Single(procs, p => p.Pid == stop.Pid);
        Assert.Equal(TestEnvironment.TestTargetPackage + ":helper", helper.Name);
        Assert.True(procs.Count >= 2, "main and helper should both be attached");
        Assert.Equal(procs.Count, procs.Select(p => p.SdbPort).Distinct().Count());

        var locals = session.GetLocals(stop.Pid, stop.ThreadId);
        Assert.StartsWith("\"helper tick ", locals.Single(l => l.Name == "message").Value);
    }

    [Fact]
    public async Task ContinueAndWait_HitsSameBreakpointAgain_WithIncreasingGeneration()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var session = await LaunchAsync(cts.Token, s => s.SetBreakpoint(new BreakpointSpec(TestEnvironment.MainActivitySource, TickLine)));

        var first = await session.WaitForStopAsync(0, StopTimeout, cts.Token);
        Assert.NotNull(first);
        var second = await session.ContinueAndWaitAsync(StopTimeout, cts.Token);
        Assert.NotNull(second);
        Assert.True(second.Generation > first.Generation);
        Assert.Equal(first.Location?.Line, second.Location?.Line);
    }

    [Fact]
    public async Task CallStack_TopFrameIsUserCode_WithExternalFramesBelow()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var session = await LaunchAsync(cts.Token, s => s.SetBreakpoint(new BreakpointSpec(TestEnvironment.MainActivitySource, TickLine)));

        var stop = await session.WaitForStopAsync(0, StopTimeout, cts.Token);
        Assert.NotNull(stop);
        var frames = session.GetCallStack(stop.Pid, stop.ThreadId);
        Assert.True(frames.Count >= 3);
        Assert.Contains("Tick", frames[0].Method);
        Assert.False(frames[0].IsExternal);
        Assert.Contains(frames, f => f.IsExternal);
    }

    [Fact]
    public async Task StepOver_AdvancesToNextLine_InSameMethod()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var stepFromLine = TestEnvironment.LineOf(TestEnvironment.MainActivitySource, "long now = Environment.TickCount64;");
        await using var session = await LaunchAsync(cts.Token, s => s.SetBreakpoint(new BreakpointSpec(TestEnvironment.MainActivitySource, stepFromLine)));

        var stop = await session.WaitForStopAsync(0, StopTimeout, cts.Token);
        Assert.NotNull(stop);
        var after = await session.StepOverAsync(stop.Pid, stop.ThreadId, StopTimeout, cts.Token);
        Assert.NotNull(after);
        Assert.Equal(StopReason.Step, after.Reason);
        Assert.Equal(stepFromLine + 1, after.Location?.Line);
        Assert.Equal(stop.ThreadId, after.ThreadId);
    }

    [Fact]
    public async Task Threads_AreListed_WhenStopped()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var session = await LaunchAsync(cts.Token, s => s.SetBreakpoint(new BreakpointSpec(TestEnvironment.MainActivitySource, TickLine)));

        var stop = await session.WaitForStopAsync(0, StopTimeout, cts.Token);
        Assert.NotNull(stop);
        var threads = session.GetThreads(stop.Pid);
        Assert.Contains(threads, t => t.Id == stop.ThreadId);
        Assert.True(threads.Count >= 2);
    }

    [Fact]
    public async Task WaitForCurrentOrNextStop_ReturnsCurrentStop_WhenAlreadyStopped()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var session = await LaunchAsync(cts.Token, s => s.SetBreakpoint(new BreakpointSpec(TestEnvironment.MainActivitySource, TickLine)));

        var first = await session.WaitForStopAsync(0, StopTimeout, cts.Token);
        Assert.NotNull(first);
        // A caller that has not seen the stop yet must get it back immediately, not a timeout.
        var again = await session.WaitForCurrentOrNextStopAsync(TimeSpan.FromSeconds(2), cts.Token);
        Assert.NotNull(again);
        Assert.Equal(first.Generation, again.Generation);
        // Plain WaitForStopAsync(after = current) keeps its strict semantics.
        Assert.Null(await session.WaitForStopAsync(first.Generation, TimeSpan.FromMilliseconds(300), cts.Token));
    }

    [Fact]
    public async Task NullValue_HasNoExpansionHandle()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var session = await LaunchAsync(cts.Token, s => s.SetBreakpoint(new BreakpointSpec(TestEnvironment.MainActivitySource, TickLine)));

        var stop = await session.WaitForStopAsync(0, StopTimeout, cts.Token);
        Assert.NotNull(stop);
        var nothing = session.GetLocals(stop.Pid, stop.ThreadId).Single(l => l.Name == "nothing");
        Assert.Contains("null", nothing.Value);
        Assert.Null(nothing.ExpansionHandle);
        Assert.False(nothing.HasChildren);
    }

    [Fact]
    public async Task AppTraces_GoToAppOutput_NotDebuggerOutput()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var session = await LaunchAsync(cts.Token, s => s.SetBreakpoint(new BreakpointSpec(TestEnvironment.MainActivitySource, TickLine)));

        Assert.NotNull(await session.WaitForStopAsync(0, StopTimeout, cts.Token));
        Assert.NotNull(await session.ContinueAndWaitAsync(StopTimeout, cts.Token));
        // Debug.WriteLine / Console.WriteLine from the debuggee: they may arrive through the
        // SDB user-log channel or logcat; either way they belong to the app output.
        var app = session.GetAppOutput(1000);
        Assert.Contains(app, l => l.Message.Contains("trace tick", StringComparison.Ordinal) || l.Message.Contains("console tick", StringComparison.Ordinal));
        // Every line is structured, not a raw logcat string.
        Assert.All(app, l => Assert.Contains(l.Level, "VDIWEF"));
        Assert.All(app, l => Assert.False(string.IsNullOrWhiteSpace(l.Tag)));
        // Filtering narrows by tag and by text.
        Assert.NotEmpty(session.GetAppOutput(1000, contains: "tick"));
        Assert.Empty(session.GetAppOutput(1000, contains: "this text appears nowhere"));
        var dbg = session.GetDebuggerOutput(1000);
        Assert.DoesNotContain(dbg, l => l.Contains("trace tick", StringComparison.Ordinal) || l.Contains("console tick", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Terminate_EndsSession_AndStopsApp()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var session = await LaunchAsync(cts.Token);
        await session.TerminateAsync(cts.Token);
        Assert.Equal(SessionState.Exited, session.State);

        var adb = new Core.Adb.AdbClient();
        var procs = await adb.ListPackageProcessesAsync(device.Serial, TestEnvironment.TestTargetPackage, cts.Token);
        Assert.Empty(procs);
        Assert.Equal("", await adb.GetPropAsync(device.Serial, "debug.mono.extra", cts.Token));
    }
}
