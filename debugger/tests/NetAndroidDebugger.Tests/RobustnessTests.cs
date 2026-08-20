using NetAndroidDebugger.Core;
using NetAndroidDebugger.Core.Adb;
using NetAndroidDebugger.Tests.Harness;
using Xunit.Abstractions;

namespace NetAndroidDebugger.Tests;

/// <summary>Edge cases: per-file breakpoint replacement, odd lines, dictionaries, unhandled exceptions, other threads, launch errors.</summary>
[Collection(DeviceCollection.Name)]
public sealed class RobustnessTests(DeviceFixture device, ITestOutputHelper output)
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(25);
    private static readonly string TickMarker = "Android.Util.Log.Debug(\"TestTarget\", message);";
    private static readonly string NowMarker = "long now = Environment.TickCount64;";
    private static readonly string CommentMarker = "// A null local (value formatting: no expansion handle) and debuggee traces";

    private static string Main => TestEnvironment.MainActivitySource;
    private int TickLine => TestEnvironment.LineOf(Main, TickMarker);
    private int NowLine => TestEnvironment.LineOf(Main, NowMarker);

    private async Task<DebugSession> LaunchAsync(CancellationToken ct, Action<DebugSession>? beforeLaunch = null)
    {
        var session = device.NewSession(output.WriteLine);
        beforeLaunch?.Invoke(session);
        await session.LaunchAsync(TestEnvironment.TestTargetApp(), device.Options(), ct);
        return session;
    }

    [Fact]
    public async Task SetBreakpoints_ReplacesAllBreakpointsOfTheFile()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var session = await LaunchAsync(cts.Token, s => s.SetBreakpoints(Main, [new BreakpointSpec(Main, NowLine)]));

        var first = await session.WaitForStopAsync(0, StopTimeout, cts.Token);
        Assert.NotNull(first);
        Assert.Equal(NowLine, first.Location?.Line);

        // Replace: only the Tick line must remain.
        var infos = session.SetBreakpoints(Main, [new BreakpointSpec(Main, TickLine)]);
        Assert.Single(infos);
        Assert.Single(session.ListBreakpoints());
        Assert.Equal(TickLine, session.ListBreakpoints()[0].Spec.Line);

        var next = await session.ContinueAndWaitAsync(StopTimeout, cts.Token);
        Assert.NotNull(next);
        Assert.Equal(TickLine, next.Location?.Line);
        // Per-file replacement with an empty list clears them.
        Assert.Empty(session.SetBreakpoints(Main, []));
        Assert.Empty(session.ListBreakpoints());
    }

    [Fact]
    public async Task Breakpoint_OnCommentLine_DoesNotCrash_SessionStaysUsable()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var commentLine = TestEnvironment.LineOf(Main, CommentMarker);
        await using var session = await LaunchAsync(cts.Token, s => s.SetBreakpoint(new BreakpointSpec(Main, commentLine)));

        // Mono either binds it to the next statement or leaves it pending; both are acceptable,
        // as long as nothing throws and the session keeps working.
        var stop = await session.WaitForStopAsync(0, TimeSpan.FromSeconds(8), cts.Token);
        var bp = Assert.Single(session.ListBreakpoints());
        output.WriteLine($"comment-line breakpoint: verified={bp.Verified} stop={stop?.Location?.Line}");
        if (stop is not null)
            Assert.True(stop.Location?.Line >= commentLine);
        else
            Assert.Equal(SessionState.Running, session.State);

        session.RemoveAllBreakpoints();
        session.SetBreakpoint(new BreakpointSpec(Main, TickLine));
        var real = stop is null
            ? await session.WaitForStopAsync(session.StopGeneration, StopTimeout, cts.Token)
            : await session.ContinueAndWaitAsync(StopTimeout, cts.Token);
        Assert.NotNull(real);
        Assert.Equal(TickLine, real.Location?.Line);
    }

    [Fact]
    public async Task DictionaryExpansion_ShowsEntries()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var session = await LaunchAsync(cts.Token, s => s.SetBreakpoint(new BreakpointSpec(Main, TickLine)));

        var stop = await session.WaitForStopAsync(0, StopTimeout, cts.Token);
        Assert.NotNull(stop);
        var sample = session.GetLocals(stop.Pid, stop.ThreadId).Single(l => l.Name == "sample");
        var map = session.ExpandVariable(sample.ExpansionHandle!).Single(c => c.Name == "Map");
        Assert.True(map.HasChildren);
        Assert.Contains("2", map.DisplayValue);
        var entries = session.ExpandVariable(map.ExpansionHandle!);
        output.WriteLine(string.Join("\n", entries.Select(e => $"{e.Name} : {e.TypeName} = {e.DisplayValue} children={e.HasChildren}")));
        Assert.True(entries.Count >= 2);
        // Entries are key/value pairs; somewhere below them the keys "one"/"two" must be visible.
        var rendered = string.Join("|", entries.Select(e => e.DisplayValue + e.Name));
        if (!rendered.Contains("one"))
        {
            var deeper = entries.Where(e => e.ExpansionHandle is not null)
                .SelectMany(e => session.ExpandVariable(e.ExpansionHandle!))
                .Select(c => c.Name + "=" + c.DisplayValue);
            rendered = string.Join("|", deeper);
        }
        Assert.Contains("one", rendered);
    }

    [Fact]
    public async Task CallStack_OfAnotherThread_IsReadable_WhenStopped()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var session = await LaunchAsync(cts.Token, s => s.SetBreakpoint(new BreakpointSpec(Main, TickLine)));

        var stop = await session.WaitForStopAsync(0, StopTimeout, cts.Token);
        Assert.NotNull(stop);
        Assert.NotEqual(1, stop.ThreadId); // Tick runs on a pool thread
        var threads = session.GetThreads(stop.Pid);
        Assert.Equal("Main", threads.Single(t => t.Id == 1).Name);
        // The main thread of an idle Activity sits in the Java looper and has no managed frames
        // (empty stack is correct there); pick another thread that reports a managed location.
        var other = threads.First(t => t.Id != stop.ThreadId && !string.IsNullOrEmpty(t.Location));
        var frames = session.GetCallStack(stop.Pid, other.Id);
        Assert.NotEmpty(frames);
        output.WriteLine($"thread {other.Id} '{other.Name}':\n" + string.Join("\n", frames.Select(f => $"#{f.Index} {f.Method} {f.File}:{f.Line} ext={f.IsExternal}")));
        // And the main thread's (possibly empty) stack must not throw.
        _ = session.GetCallStack(stop.Pid, 1);
    }

    [Fact]
    public async Task UnhandledException_IsReported_ThenAppExits()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var adb = new AdbClient();
        var pkg = TestEnvironment.TestTargetPackage;
        await adb.ShellAsync(device.Serial, $"run-as {pkg} rm -f files/crash-on-tick", cts.Token);
        await using var session = await LaunchAsync(cts.Token);
        try
        {
            // Arm the hook once the app runs; the next tick throws on the timer thread.
            await adb.ShellAsync(device.Serial, $"run-as {pkg} touch files/crash-on-tick", cts.Token);

            var stop = await session.WaitForStopAsync(0, TimeSpan.FromSeconds(30), cts.Token);
            Assert.NotNull(stop);
            Assert.Equal(StopReason.UnhandledException, stop.Reason);
            Assert.Contains("ApplicationException", stop.ExceptionType);
            // Details are captured at stop time (the process dies right after an unhandled exception).
            var details = session.GetExceptionDetails();
            Assert.NotNull(details);
            Assert.Contains("ApplicationException", details.Value.Type);
            // At an unhandled-exception stop the thread backtrace is the dispatch/rethrow point
            // (ExceptionDispatchInfo.Throw), not the original throw site — that lives in the
            // exception object's own StackTrace, which needs a debuggee invocation the dying
            // process can no longer serve. So assert a non-empty trace, not a specific frame.
            Assert.NotEmpty(details.Value.StackTrace);
            output.WriteLine($"exception trace:\n{details.Value.StackTrace}\nmessage: '{details.Value.Message}'");
            // Message needs a field read that may not resolve before the process dies; best-effort.
            if (details.Value.Message.Length > 0)
                Assert.Contains("unhandled failure requested", details.Value.Message);

            // One Continue must be enough: further unhandled exceptions raised while the process
            // dies are resumed automatically by the engine, so the crashing process reaches its
            // end without further intervention. (The session only reports Exited once *every*
            // process is gone, and the sticky :helper service can outlive the main one.)
            session.Continue();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
            ProcessSnapshot? mainProc;
            do
            {
                mainProc = session.GetProcesses().FirstOrDefault(p => p.Name == pkg);
                if (session.State == SessionState.Exited || mainProc is null || mainProc.HasExited) break;
                await Task.Delay(250, cts.Token);
            } while (DateTime.UtcNow < deadline);
            output.WriteLine($"after continue: state={session.State} main={mainProc}");
            Assert.True(session.State == SessionState.Exited || mainProc is null || mainProc.HasExited,
                $"the crashing process should have died; state={session.State} main={mainProc}");
            // Nothing is suspended once the crashing process is gone, so the session must not
            // still claim to be stopped.
            Assert.False(session.State == SessionState.Stopped && !session.GetProcesses().Any(p => p.IsStopped && !p.HasExited),
                $"session still reports Stopped with nothing suspended; processes: {string.Join(", ", session.GetProcesses())}");
            // The details still describe the first (real) exception, not a teardown one.
            var still = session.GetExceptionDetails();
            Assert.NotNull(still);
            Assert.Contains("ApplicationException", still.Value.Type);
        }
        finally
        {
            await adb.ShellAsync(device.Serial, $"run-as {pkg} rm -f files/crash-on-tick", CancellationToken.None);
        }
    }

    [Fact]
    public async Task ProcessSpawnedWhileMainIsStopped_IsAttachedOnItsOwnPort()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var lateLine = TestEnvironment.LineOf(TestEnvironment.HelperServiceSource, "Android.Util.Log.Verbose(\"TestTarget\", $\"late compute {value}\");");
        await using var session = await LaunchAsync(cts.Token, s =>
        {
            s.SetBreakpoint(new BreakpointSpec(Main, TickLine));
            s.SetBreakpoint(new BreakpointSpec(TestEnvironment.HelperServiceSource, lateLine));
        });

        // Suspend the app first: the third process must be attached even though the main process
        // (which normally starts helpers) is frozen — Android starts it for the broadcast.
        var stop = await session.WaitForStopAsync(0, StopTimeout, cts.Token);
        Assert.NotNull(stop);
        Assert.Equal(SessionState.Stopped, session.State);

        var pkg = TestEnvironment.TestTargetPackage;
        // `am broadcast` waits for the receiver to return, and our breakpoint stops it inside the
        // receiver — so waiting for the command here would deadlock the test against itself.
        var adb = new AdbClient();
        _ = Task.Run(async () =>
        {
            try { await adb.ShellAsync(device.Serial, $"am broadcast -a {pkg}.SPAWN_LATE -p {pkg}", CancellationToken.None, TimeSpan.FromMinutes(2)); }
            catch (Exception ex) { output.WriteLine($"broadcast command ended: {ex.Message}"); }
        }, CancellationToken.None);

        // The new process reads the (still fresh) debug property, waits for us, and gets the next port.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        ProcessSnapshot? late;
        do
        {
            late = session.GetProcesses().FirstOrDefault(p => p.Name.EndsWith(":late", StringComparison.Ordinal));
            if (late is not null) break;
            await Task.Delay(500, cts.Token);
        } while (DateTime.UtcNow < deadline);

        var processes = session.GetProcesses();
        output.WriteLine(string.Join("\n", processes.Select(p => $"{p.Pid} {p.Name} port={p.SdbPort} stopped={p.IsStopped} exited={p.HasExited}")));
        Assert.NotNull(late);
        Assert.True(processes.Count >= 3, "main, :helper and :late should all be attached");
        Assert.Equal(processes.Count, processes.Select(p => p.SdbPort).Distinct().Count());

        // And it is really debuggable: its breakpoint is hit inside the receiver.
        var lateStop = await session.WaitForStopAsync(stop.Generation, StopTimeout, cts.Token);
        Assert.NotNull(lateStop);
        Assert.Equal(late.Pid, lateStop.Pid);
        Assert.Equal(lateLine, lateStop.Location?.Line);
        Assert.Equal("123", session.GetLocals(lateStop.Pid, lateStop.ThreadId).Single(l => l.Name == "value").Value);
    }

    [Fact]
    public async Task SteppingOneProcess_LeavesTheOtherStopped()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var helperTick = TestEnvironment.LineOf(TestEnvironment.HelperServiceSource, "Android.Util.Log.Debug(\"TestTarget\", message);");
        await using var session = await LaunchAsync(cts.Token, s =>
        {
            s.SetBreakpoint(new BreakpointSpec(Main, TickLine));
            s.SetBreakpoint(new BreakpointSpec(TestEnvironment.HelperServiceSource, helperTick));
        });

        // Wait until both processes are sitting at their own breakpoint.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        long generation = 0;
        while (DateTime.UtcNow < deadline)
        {
            var stop = await session.WaitForStopAsync(generation, StopTimeout, cts.Token);
            if (stop is null) break;
            generation = stop.Generation;
            var stopped = session.GetProcesses().Where(p => p.IsStopped).ToList();
            if (stopped.Count >= 2) break;
            // Resuming would release the one already stopped; just wait for the other's stop.
        }
        var processes = session.GetProcesses();
        output.WriteLine(string.Join("\n", processes.Select(p => $"{p.Pid} {p.Name} stopped={p.IsStopped}")));
        var both = processes.Where(p => p.IsStopped).ToList();
        Assert.True(both.Count >= 2, "both the main and the helper process should be stopped");

        var main = both.Single(p => p.Name == TestEnvironment.TestTargetPackage);
        var helper = both.Single(p => p.Name.EndsWith(":helper", StringComparison.Ordinal));

        // Step in whichever process reported the last stop; the other must stay where it was.
        var last = session.LastStop!;
        var other = last.Pid == main.Pid ? helper : main;
        var stepped = await session.StepOverAsync(last.Pid, last.ThreadId, StopTimeout, cts.Token);
        Assert.NotNull(stepped);
        Assert.Equal(last.Pid, stepped.Pid);
        Assert.True(session.GetProcesses().Single(p => p.Pid == other.Pid).IsStopped, "the other process must still be stopped");
        // And it is still inspectable while the other process moved.
        Assert.NotEmpty(session.GetThreads(other.Pid));
    }

    [Fact]
    public async Task ExceptionFilters_CanBeNarrowedAndCleared()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var session = await LaunchAsync(cts.Token, s => s.SetExceptionFilters(
            new ExceptionFilters(true, ["System.FormatException", "System.InvalidOperationException"])));

        // TestTarget throws InvalidOperationException every fifth tick; the unrelated filter
        // in the list must not prevent that from being caught.
        var stop = await session.WaitForStopAsync(0, TimeSpan.FromSeconds(45), cts.Token);
        Assert.NotNull(stop);
        Assert.Equal(StopReason.Exception, stop.Reason);
        Assert.Contains("InvalidOperationException", stop.ExceptionType);

        // Clearing the filters must stop the exception stops.
        session.SetExceptionFilters(new ExceptionFilters(true, []));
        Assert.Empty(session.GetExceptionFilters().FirstChanceTypes ?? []);
        var next = await session.ContinueAndWaitAsync(TimeSpan.FromSeconds(20), cts.Token);
        output.WriteLine($"after clearing filters: {next?.Reason.ToString() ?? "no stop (expected)"}");
        Assert.True(next is null || next.Reason != StopReason.Exception,
            $"no first-chance stop expected after clearing, got {next?.Reason} at {next?.Location?.Method}");
    }

    [Fact]
    public async Task EvaluationOptions_WithoutToStringCalls_StillReadsValues()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var session = await LaunchAsync(cts.Token, s =>
        {
            // Fields only: nothing is invoked in the debuggee, which is the safe mode for
            // targets where a getter may block.
            s.SetEvaluationOptions(allowToStringCalls: false, allowTargetInvoke: false);
            s.SetBreakpoint(new BreakpointSpec(Main, TickLine));
        });

        var stop = await session.WaitForStopAsync(0, StopTimeout, cts.Token);
        Assert.NotNull(stop);
        var locals = session.GetLocals(stop.Pid, stop.ThreadId);
        output.WriteLine(string.Join("\n", locals.Select(l => $"{l.Name} : {l.TypeName} = {l.DisplayValue}")));

        // Primitives and strings still read correctly without any invocation.
        Assert.StartsWith("\"tick ", locals.Single(l => l.Name == "message").Value);
        Assert.True(long.TryParse(locals.Single(l => l.Name == "now").Value, out _));

        // Object expansion still works; it just reports fields rather than invoking getters.
        var sample = locals.Single(l => l.Name == "sample");
        var children = session.ExpandVariable(sample.ExpansionHandle!);
        output.WriteLine("children: " + string.Join(", ", children.Select(c => c.Name)));
        Assert.NotEmpty(children);
        var options = session.GetEvaluationOptions();
        Assert.False(options.AllowToStringCalls);
        Assert.False(options.AllowTargetInvoke);
    }

    [Fact]
    public async Task Launch_UnknownPackage_FailsCleanly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await using var session = device.NewSession(output.WriteLine);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => session.LaunchAsync(new AppTarget("no.such.package.here"), device.Options(), cts.Token));
        output.WriteLine(ex.GetType().Name + ": " + ex.Message);
        Assert.Equal(SessionState.Exited, session.State);
        Assert.Contains("no.such.package.here", ex.Message);
        Assert.Equal("", await new AdbClient().GetPropAsync(device.Serial, "debug.mono.extra", cts.Token));
    }

    [Fact]
    public async Task Launch_UnknownDeviceSerial_FailsCleanly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await using var session = device.NewSession(output.WriteLine);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => session.LaunchAsync(TestEnvironment.TestTargetApp(), new LaunchOptions("no-such-serial"), cts.Token));
        output.WriteLine(ex.GetType().Name + ": " + ex.Message);
        Assert.Equal(SessionState.Exited, session.State);
    }
}
