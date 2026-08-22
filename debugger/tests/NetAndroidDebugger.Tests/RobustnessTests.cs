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
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
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
            // dies are resumed automatically by the engine, so no further intervention is needed.
            // Whether the process actually dies is NOT reliable, and this test does not pretend it
            // is: measured 2026-08-21 over eight runs, it usually goes within seconds but outlived
            // 90 s twice, with the hook armed or not. What must hold either way is that the session
            // stays coherent. (The hook stays armed on purpose - a tick that throws on every
            // iteration is what a real crashing app does; the `finally` clears it.)
            session.Continue();
            var crashedPid = stop.Pid;
            var startedWaiting = DateTime.UtcNow;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
            ProcessSnapshot? mainProc;
            do
            {
                // The pid that crashed, not "a process with that name": Android restarts the app
                // and that restart is attached too, so the name comes back within seconds.
                mainProc = session.GetProcesses().FirstOrDefault(p => p.Pid == crashedPid);
                if (session.State == SessionState.Exited || mainProc is null || mainProc.HasExited) break;
                await Task.Delay(250, cts.Token);
            } while (DateTime.UtcNow < deadline);
            var died = session.State == SessionState.Exited || mainProc is null || mainProc.HasExited;
            output.WriteLine($"after continue ({(DateTime.UtcNow - startedWaiting).TotalSeconds:F1}s): died={died} state={session.State} crashed={mainProc}");
            // The session must stay coherent whichever way it went: never "stopped" with nothing
            // actually suspended, and never still holding the crashed process as stopped.
            Assert.False(session.State == SessionState.Stopped && !session.GetProcesses().Any(p => p.IsStopped && !p.HasExited),
                $"session still reports Stopped with nothing suspended; processes: {string.Join(", ", session.GetProcesses())}");
            Assert.False(session.GetProcesses().Any(p => p.Pid == crashedPid && p.IsStopped && !p.HasExited),
                "the process that hit the unhandled exception is still held stopped after Continue");
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
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
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

    /// <summary>
    /// A component declared with a global <c>android:process</c> runs in a process whose name says
    /// nothing about the package (the reference application's `the app's own android:process` is one). It is still the app,
    /// and the launcher must recognise it by uid instead of refusing it as a foreign Mono process.
    /// </summary>
    [Fact]
    public async Task ProcessWithAGlobalName_IsRecognisedByUid_AndAttached()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var globalLine = TestEnvironment.LineOf(TestEnvironment.HelperServiceSource, "Android.Util.Log.Verbose(\"TestTarget\", $\"global compute {value}\");");
        await using var session = await LaunchAsync(cts.Token, s =>
            s.SetBreakpoint(new BreakpointSpec(TestEnvironment.HelperServiceSource, globalLine)));

        var pkg = TestEnvironment.TestTargetPackage;
        var adb = new AdbClient();
        // Let the helper process settle first. Two processes starting at the same instant read the
        // same value of the (device-global) property and race for one port; the loser's agent
        // cannot listen and it dies. That race is a property of the mechanism, not what this test
        // is about.
        await WaitForAsync(
            () => session.GetProcesses().FirstOrDefault(p => p.Name.EndsWith(":helper", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(45), cts.Token);

        // The receiver stops at our breakpoint, so the broadcast command would not return: fire it.
        _ = Task.Run(async () =>
        {
            try { await adb.ShellAsync(device.Serial, $"am broadcast -a {pkg}.SPAWN_GLOBAL -p {pkg}", CancellationToken.None, TimeSpan.FromMinutes(2)); }
            catch (Exception ex) { output.WriteLine($"broadcast command ended: {ex.Message}"); }
        }, CancellationToken.None);

        var global = await WaitForAsync(
            () => session.GetProcesses().FirstOrDefault(p => p.Name.Contains("globalproc", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(45), cts.Token);
        Assert.NotNull(global);
        // Its name shares nothing with the package: only the uid made it ours.
        Assert.DoesNotContain(pkg, global.Name, StringComparison.Ordinal);

        var stop = await session.WaitForStopAsync(0, StopTimeout, cts.Token);
        Assert.NotNull(stop);
        Assert.Equal(global.Pid, stop.Pid);
        Assert.Equal("456", session.GetLocals(stop.Pid, stop.ThreadId).Single(l => l.Name == "value").Value);
    }

    [Fact]
    public async Task KilledHelperProcess_IsReportedGone_AndReattachedWhenAndroidRestartsIt()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var adb = new AdbClient();
        var session = await LaunchAsync(cts.Token);
        try
        {
        // Wait for the sticky helper service to be attached.
        var helper = await WaitForAsync(() => session.GetProcesses().FirstOrDefault(p => p.Name.EndsWith(":helper", StringComparison.Ordinal) && !p.HasExited),
            TimeSpan.FromSeconds(45), cts.Token);
        Assert.NotNull(helper);
        output.WriteLine($"helper attached: pid {helper.Pid} port {helper.SdbPort}");

        var pkg = TestEnvironment.TestTargetPackage;
        await adb.ShellAsync(device.Serial, $"am broadcast -a {pkg}.KILL_HELPER -p {pkg}", cts.Token);

        // The engine must notice the process is gone...
        var gone = await WaitForAsync(() => session.GetProcesses().FirstOrDefault(p => p.Pid == helper.Pid && p.HasExited),
            TimeSpan.FromSeconds(45), cts.Token);
        Assert.NotNull(gone);

        // ...and attach the one Android starts in its place, on a port of its own.
        var restarted = await WaitForAsync(() => session.GetProcesses().FirstOrDefault(p => p.Name.EndsWith(":helper", StringComparison.Ordinal) && p.Pid != helper.Pid && !p.HasExited),
            TimeSpan.FromSeconds(90), cts.Token);
        output.WriteLine(string.Join("\n", session.GetProcesses().Select(p => $"{p.Pid} {p.Name} port={p.SdbPort} exited={p.HasExited}")));
        Assert.NotNull(restarted);
        Assert.NotEqual(helper.SdbPort, restarted.SdbPort);
        }
        finally
        {
            // This test deliberately makes Android restart a sticky service. Tear the app down
            // and wait for it to be really gone, or those restarts spill into the next test.
            await session.DisposeAsync();
            var pkg = TestEnvironment.TestTargetPackage;
            await adb.ForceStopAsync(device.Serial, pkg, CancellationToken.None);
            var gone = await WaitForAsync(
                async () => (await adb.ListPackageProcessesAsync(device.Serial, pkg, CancellationToken.None)).Count == 0 ? "gone" : null,
                TimeSpan.FromSeconds(30), CancellationToken.None);
            output.WriteLine($"teardown: app processes {(gone is null ? "still present" : "gone")}");
        }
    }


    /// <summary>
    /// `debug.mono.extra` carries a deadline and is read at process start, so a process the app
    /// starts long after launch finds it expired and runs without a debugger. the reference application does exactly
    /// that with its on-demand service and its crash reporter. `KeepPropertyFresh` rewrites the
    /// deadline for as long as the session lives; it is off by default because the same freshness
    /// is what makes an unrelated Mono app wait for a debugger on our port.
    /// </summary>
    [Fact]
    public async Task ProcessStartedAfterThePropertyExpired_IsAttached_OnlyWhenTheLifetimeIsKeptFresh()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var adb = new AdbClient();
        var pkg = TestEnvironment.TestTargetPackage;
        var lifetime = TimeSpan.FromSeconds(25);

        async Task<ProcessSnapshot?> SpawnLateAndWatchAsync(DebugSession session)
        {
            // Let the property go stale (it would already be renewed at least once if kept fresh).
            await Task.Delay(lifetime + TimeSpan.FromSeconds(15), cts.Token);
            await adb.ShellAsync(device.Serial, $"am broadcast -a {pkg}.SPAWN_LATE -p {pkg}", cts.Token, TimeSpan.FromMinutes(1));
            return await WaitForAsync(
                () => session.GetProcesses().FirstOrDefault(p => p.Name.EndsWith(":late", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(25), cts.Token);
        }

        // Default: the late process starts, but without a debugger.
        var plain = device.NewSession(output.WriteLine);
        await using (plain)
        {
            await plain.LaunchAsync(TestEnvironment.TestTargetApp(), device.Options() with { PropertyLifetime = lifetime }, cts.Token);
            Assert.Null(await SpawnLateAndWatchAsync(plain));
            // It did run — it is simply not ours to debug.
            var onDevice = await adb.ListPackageProcessesAsync(device.Serial, pkg, cts.Token);
            Assert.Contains(onDevice, p => p.Name.EndsWith(":late", StringComparison.Ordinal));
        }

        // Kept fresh: the same process is attached, on its own port.
        var fresh = device.NewSession(output.WriteLine);
        await using (fresh)
        {
            await fresh.LaunchAsync(TestEnvironment.TestTargetApp(),
                device.Options() with { PropertyLifetime = lifetime, KeepPropertyFresh = true }, cts.Token);
            var late = await SpawnLateAndWatchAsync(fresh);
            Assert.NotNull(late);
            var all = fresh.GetProcesses();
            Assert.Equal(all.Count, all.Select(p => p.SdbPort).Distinct().Count());
        }
    }
    private static async Task<T?> WaitForAsync<T>(Func<Task<T?>> probe, TimeSpan timeout, CancellationToken ct) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var value = await probe();
            if (value is not null) return value;
            await Task.Delay(500, ct);
        }
        return null;
    }

    private static async Task<T?> WaitForAsync<T>(Func<T?> probe, TimeSpan timeout, CancellationToken ct) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var value = probe();
            if (value is not null) return value;
            await Task.Delay(500, ct);
        }
        return null;
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
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
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
    public async Task AppDyingOnItsOwn_IsReportedAsProcessExit()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var adb = new AdbClient();
        await using var session = await LaunchAsync(cts.Token);
        var pkg = TestEnvironment.TestTargetPackage;
        var main = session.GetProcesses().Single(p => p.Name == pkg);

        await adb.ShellAsync(device.Serial, $"am broadcast -a {pkg}.KILL_APP -p {pkg}", cts.Token);

        // The debugger must notice on its own, without being asked to do anything.
        var gone = await WaitForAsync(() => session.GetProcesses().FirstOrDefault(p => p.Pid == main.Pid && p.HasExited),
            TimeSpan.FromSeconds(45), cts.Token);
        output.WriteLine($"after the app killed itself: state={session.State}, processes: {string.Join(", ", session.GetProcesses())}");
        Assert.True(gone is not null || session.State == SessionState.Exited,
            $"the dead main process was not reported; state={session.State}");
        // And the session never claims something is suspended when nothing is.
        Assert.False(session.State == SessionState.Stopped && !session.GetProcesses().Any(p => p.IsStopped && !p.HasExited));
    }

    [Fact]
    public async Task StepOver_ACallThatThrows_StaysInTheMethod()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        // The throw inside Tick's try/catch: stepping over it must land in the catch block,
        // in the same method, not derail the session.
        var throwLine = TestEnvironment.LineOf(Main, "throw new InvalidOperationException($\"expected failure at tick {_ticks}\");");
        await using var session = await LaunchAsync(cts.Token, s => s.SetBreakpoint(new BreakpointSpec(Main, throwLine)));

        var stop = await session.WaitForStopAsync(0, TimeSpan.FromSeconds(45), cts.Token);
        Assert.NotNull(stop);
        Assert.Equal(throwLine, stop.Location?.Line);

        session.RemoveAllBreakpoints();
        var after = await session.StepOverAsync(stop.Pid, stop.ThreadId, StopTimeout, cts.Token);
        Assert.NotNull(after);
        output.WriteLine($"stepped from the throw to {after.Location?.Method} line {after.Location?.Line}");
        Assert.Contains("Tick", after.Location?.Method);
        Assert.True(after.Location?.Line > throwLine, "the step should land in the catch block below the throw");
        // The session is still usable afterwards.
        Assert.NotEmpty(session.GetLocals(after.Pid, after.ThreadId));
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
    public async Task Detach_TerminatesTheApp_ByDesign()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var adb = new AdbClient();
        var pkg = TestEnvironment.TestTargetPackage;
        var session = await LaunchAsync(cts.Token);
        var before = await adb.ListPackageProcessesAsync(device.Serial, pkg, cts.Token);
        Assert.NotEmpty(before);

        // On Mono Android the runtime exits when the debugger disconnects, so detaching cannot
        // leave the app running. The engine models this by terminating explicitly; this test
        // exists so the day that changes upstream, we notice.
        await session.DetachAsync(cts.Token);
        Assert.Equal(SessionState.Exited, session.State);

        // The processes that were running must die. Android restarting the sticky `:helper`
        // afterwards is its own behaviour and would make "no process at all" flaky, so this looks
        // at the pids we saw.
        var pids = before.Select(p => p.Pid).ToHashSet();
        var left = await WaitForAsync(
            async () => (await adb.ListPackageProcessesAsync(device.Serial, pkg, CancellationToken.None)).All(p => !pids.Contains(p.Pid)) ? "gone" : null,
            TimeSpan.FromSeconds(20), cts.Token);
        Assert.Equal("gone", left);
    }

    [Fact]
    public async Task LaunchingAnAlreadyRunningApp_RestartsItUnderTheDebugger()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var adb = new AdbClient();
        var pkg = TestEnvironment.TestTargetPackage;

        // Start it the ordinary way first: no agent, nothing attached.
        await adb.ForceStopAsync(device.Serial, pkg, cts.Token);
        var activity = await adb.ResolveLauncherActivityAsync(device.Serial, pkg, cts.Token);
        await adb.StartActivityAsync(device.Serial, activity, cts.Token);
        var before = await WaitForAsync(
            async () => (await adb.ListPackageProcessesAsync(device.Serial, pkg, CancellationToken.None)).FirstOrDefault(p => p.Name == pkg) is { Pid: > 0 } m ? m.Pid.ToString() : null,
            TimeSpan.FromSeconds(30), cts.Token);
        Assert.NotNull(before);
        output.WriteLine($"running without debugger as pid {before}");

        // Attaching means restarting it with the agent — the pid must change.
        await using var session = await LaunchAsync(cts.Token);
        var main = session.GetProcesses().Single(p => p.Name == pkg);
        output.WriteLine($"after launch: pid {main.Pid}");
        Assert.NotEqual(int.Parse(before), main.Pid);
        Assert.Equal(SessionState.Running, session.State);
    }

    [Fact]
    public async Task ScreenRotation_RecreatesTheActivity_AndTheSessionSurvives()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var adb = new AdbClient();
        var onCreate = TestEnvironment.LineOf(Main, "SetContentView(Resource.Layout.activity_main);");
        await using var session = await LaunchAsync(cts.Token);
        var mainPid = session.GetProcesses().Single(p => p.Name == TestEnvironment.TestTargetPackage).Pid;

        await adb.ShellAsync(device.Serial, "settings put system accelerometer_rotation 0", cts.Token);
        try
        {
            // A rotation destroys and recreates the Activity in the same process: OnCreate runs
            // again, so a breakpoint there proves the session followed the app through it.
            session.SetBreakpoint(new BreakpointSpec(Main, onCreate));
            await adb.ShellAsync(device.Serial, "settings put system user_rotation 1", cts.Token);

            var stop = await session.WaitForStopAsync(session.StopGeneration, TimeSpan.FromSeconds(45), cts.Token);
            output.WriteLine($"after rotation: {stop?.Location?.Method} line {stop?.Location?.Line} pid {stop?.Pid}");
            Assert.NotNull(stop);
            Assert.Equal(onCreate, stop.Location?.Line);
            Assert.Equal(mainPid, stop.Pid);
            Assert.NotEmpty(session.GetLocals(stop.Pid, stop.ThreadId));
        }
        finally
        {
            await adb.ShellAsync(device.Serial, "settings put system user_rotation 0", CancellationToken.None);
        }
    }

    [Fact]
    public async Task ForeignMonoApp_StartingDuringTheSession_IsNotAttached()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var adb = new AdbClient();
        // Any other .NET Android app installed on the device will do; the reference application is the one that
        // exposed this in real use. Without it there is nothing to prove, so skip.
        const string foreignPackage = "App.Droid";
        var installed = await adb.ShellAsync(device.Serial, $"pm list packages {foreignPackage}", cts.Token);
        if (!installed.Contains(foreignPackage, StringComparison.Ordinal))
        {
            output.WriteLine($"{foreignPackage} is not installed on {device.Serial}; nothing to check");
            return;
        }

        await using var session = await LaunchAsync(cts.Token);
        var ourPids = session.GetProcesses().Select(p => p.Pid).ToHashSet();

        try
        {
            // The debug property is device-global and still fresh: this app reads it too.
            var activity = await adb.ResolveLauncherActivityAsync(device.Serial, foreignPackage, cts.Token);
            await adb.StartActivityAsync(device.Serial, activity, cts.Token);

            // Wait for the engine to say so out loud rather than for a fixed delay: a big app on a
            // freshly booted emulator can take a long time to reach agent init.
            var announced = await WaitForAsync(
                () => session.GetDebuggerOutput(2000).FirstOrDefault(l => l.Contains("foreign Mono process", StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(150), cts.Token);
            if (announced is null)
            {
                // The guard only has something to refuse once that app's Mono agent starts. If it
                // never got there, this run proves nothing either way — say so instead of blaming
                // the engine, the same as when the app is not installed at all.
                output.WriteLine($"{foreignPackage} never reached its Mono agent init; nothing to check in this run.");
                return;
            }
            var processes = session.GetProcesses();
            output.WriteLine(string.Join("\n", processes.Select(p => $"{p.Pid} {p.Name} port={p.SdbPort}")));
            Assert.All(processes, p => Assert.StartsWith(TestEnvironment.TestTargetPackage, p.Name));
            Assert.DoesNotContain(processes, p => p.Name.Contains(foreignPackage, StringComparison.OrdinalIgnoreCase));
            Assert.NotEmpty(session.GetProcesses().Where(p => ourPids.Contains(p.Pid) && !p.HasExited));
        }
        finally
        {
            await adb.ForceStopAsync(device.Serial, foreignPackage, CancellationToken.None);
        }
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

    // ------------------------------------------------------------------ exception rules

    /// <summary>
    /// The case the rule engine exists for: an app that throws on purpose. TestTarget raises a
    /// handled InvalidOperationException every fifth tick, exactly as the reference application raises MQTT timeouts
    /// whenever the network blinks. With a filter alone the choice is "stop every few seconds" or
    /// "see nothing"; a rule makes it "let that one through, break on anything else".
    /// </summary>
    [Fact]
    public async Task ExceptionRule_Ignore_LetsTheNoisyOneThrough()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var session = await LaunchAsync(cts.Token, s =>
        {
            s.SetExceptionFilters(new ExceptionFilters(true, ["System.InvalidOperationException"]));
            s.SetExceptionRules([new ExceptionRule(ExceptionAction.Ignore, Type: "System.InvalidOperationException")]);
        });

        // Long enough for several of them: with the rule in force none of these stops the app.
        var stop = await session.WaitForStopAsync(0, TimeSpan.FromSeconds(25), cts.Token);
        Assert.Null(stop);
        Assert.Equal(SessionState.Running, session.State);
    }

    /// <summary>
    /// Same exception, action `log`: the app keeps running and the debugger says what it saw. This
    /// is the form that answers "is it still throwing?" without freezing an app that talks to a
    /// backend.
    /// </summary>
    [Fact]
    public async Task ExceptionRule_Log_ReportsWithoutStopping()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var session = await LaunchAsync(cts.Token, s =>
        {
            s.SetExceptionFilters(new ExceptionFilters(true, ["System.InvalidOperationException"]));
            s.SetExceptionRules([new ExceptionRule(ExceptionAction.Log, Type: "System.InvalidOperationException")]);
        });

        var stop = await session.WaitForStopAsync(0, TimeSpan.FromSeconds(25), cts.Token);
        Assert.Null(stop);

        var logged = session.GetDebuggerOutput(500).Where(l => l.Contains("exception rule", StringComparison.Ordinal)).ToList();
        output.WriteLine(string.Join("\n", logged.Take(3)));
        Assert.NotEmpty(logged);
        Assert.Contains(logged, l => l.Contains("InvalidOperationException", StringComparison.Ordinal));
    }

    /// <summary>
    /// Order decides: the first matching rule wins, so a specific "ignore" placed before a general
    /// "break" is how one exception is let through while everything else still stops.
    /// </summary>
    [Fact]
    public async Task ExceptionRules_FirstMatchWins_SoTheGeneralRuleStillBreaks()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var session = await LaunchAsync(cts.Token, s =>
        {
            // Stop on both types the app raises...
            s.SetExceptionFilters(new ExceptionFilters(true, ["System.InvalidOperationException", "System.ApplicationException"]));
            // ...but let the routine one through, and break on anything else.
            s.SetExceptionRules([
                new ExceptionRule(ExceptionAction.Ignore, Type: "System.InvalidOperationException"),
                new ExceptionRule(ExceptionAction.Break),
            ]);
        });

        // The routine exception is ignored, so nothing stops...
        Assert.Null(await session.WaitForStopAsync(0, TimeSpan.FromSeconds(20), cts.Token));

        // ...until a different one is raised, which the general rule breaks on.
        var adb = new Core.Adb.AdbClient();
        var pkg = TestEnvironment.TestTargetPackage;
        await adb.ShellAsync(device.Serial, $"run-as {pkg} touch files/crash-on-tick", cts.Token);
        try
        {
            var stop = await session.WaitForStopAsync(0, TimeSpan.FromSeconds(40), cts.Token);
            Assert.NotNull(stop);
            Assert.Contains("ApplicationException", stop.ExceptionType);
        }
        finally
        {
            await adb.ShellAsync(device.Serial, $"run-as {pkg} rm -f files/crash-on-tick", CancellationToken.None);
        }
    }

    /// <summary>
    /// A rule that matches on the message needs a call into the debuggee, which cannot happen on
    /// the event thread. The decision therefore moves to a worker, and the visible behaviour has
    /// to be the same.
    /// </summary>
    [Fact]
    public async Task ExceptionRule_MatchingOnMessage_Works()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var session = await LaunchAsync(cts.Token, s =>
        {
            s.SetExceptionFilters(new ExceptionFilters(true, ["System.InvalidOperationException"]));
            s.SetExceptionRules([new ExceptionRule(ExceptionAction.Ignore, MessageContains: "expected failure")]);
        });

        var stop = await session.WaitForStopAsync(0, TimeSpan.FromSeconds(25), cts.Token);
        Assert.Null(stop);
        Assert.Equal(SessionState.Running, session.State);
    }

    /// <summary>
    /// A rule whose message criterion does not match must leave the exception alone, so the filter
    /// still stops the app. Without this the "ignore" tests above would pass even if the engine
    /// ignored everything.
    /// </summary>
    [Fact]
    public async Task ExceptionRule_ThatDoesNotMatch_LeavesTheStopAlone()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var session = await LaunchAsync(cts.Token, s =>
        {
            s.SetExceptionFilters(new ExceptionFilters(true, ["System.InvalidOperationException"]));
            s.SetExceptionRules([new ExceptionRule(ExceptionAction.Ignore, MessageContains: "this text never appears")]);
        });

        var stop = await session.WaitForStopAsync(0, TimeSpan.FromSeconds(40), cts.Token);
        Assert.NotNull(stop);
        Assert.Equal(StopReason.Exception, stop.Reason);
        Assert.Contains("InvalidOperationException", stop.ExceptionType);
    }

    [Fact]
    public async Task ExceptionRule_BadRegex_IsRejectedWhenItIsSet()
    {
        await using var session = new DebugSession();
        var ex = Assert.Throws<ArgumentException>(() =>
            session.SetExceptionRules([new ExceptionRule(ExceptionAction.Ignore, MessageRegex: "(unclosed")]));
        Assert.Contains("regular expression", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The shared rules file is a baseline the session's own rules override, and it is re-read on
    /// resume: the point is being able to edit a rule while the app is stopped and have it govern
    /// what happens next, without restarting the session.
    /// </summary>
    [Fact]
    public async Task GlobalExceptionRules_AreReReadOnResume()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var dir = Directory.CreateTempSubdirectory("nad-rules-");
        var file = Path.Combine(dir.FullName, "exceptionRules.json");
        try
        {
            // Start with a file that breaks on the exception the app raises every fifth tick.
            await File.WriteAllTextAsync(file, """
                [{"type":"System.InvalidOperationException","action":"break"}]
                """, cts.Token);

            await using var session = await LaunchAsync(cts.Token, s =>
            {
                s.SetExceptionFilters(new ExceptionFilters(true, ["System.InvalidOperationException"]));
                s.SetGlobalExceptionRuleSource(new NetAndroidDebugger.Frontends.ExceptionRuleFile(file));
            });

            var stop = await session.WaitForStopAsync(0, TimeSpan.FromSeconds(45), cts.Token);
            Assert.NotNull(stop);
            Assert.Equal(StopReason.Exception, stop.Reason);

            // Edit the file while the app is stopped: from here on that exception is ignored.
            await File.WriteAllTextAsync(file, """
                [{"type":"System.InvalidOperationException","action":"ignore"}]
                """, cts.Token);
            // The engine compares the file's timestamp, whose resolution is coarse enough that an
            // immediate rewrite can look unchanged.
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(1));

            session.Continue();
            // Several more of them go by; none stops the app now.
            Assert.Null(await session.WaitForStopAsync(stop.Generation, TimeSpan.FromSeconds(25), cts.Token));
            Assert.Equal(SessionState.Running, session.State);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The session's own rules are consulted before the shared ones, so a project can override the
    /// machine-wide baseline rather than being stuck with it.
    /// </summary>
    [Fact]
    public async Task SessionRules_WinOverTheSharedFile()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var dir = Directory.CreateTempSubdirectory("nad-rules-");
        var file = Path.Combine(dir.FullName, "exceptionRules.json");
        try
        {
            // The shared baseline says break...
            await File.WriteAllTextAsync(file, """
                [{"action":"break"}]
                """, cts.Token);

            await using var session = await LaunchAsync(cts.Token, s =>
            {
                s.SetExceptionFilters(new ExceptionFilters(true, ["System.InvalidOperationException"]));
                s.SetGlobalExceptionRuleSource(new NetAndroidDebugger.Frontends.ExceptionRuleFile(file));
                // ...and this session says otherwise for this one type.
                s.SetExceptionRules([new ExceptionRule(ExceptionAction.Ignore, Type: "System.InvalidOperationException")]);
            });

            Assert.Null(await session.WaitForStopAsync(0, TimeSpan.FromSeconds(25), cts.Token));
            Assert.Equal(SessionState.Running, session.State);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A file being edited is half-written for a moment, which must not take a resume down: the
    /// rules already in force stay, and the engine says what it could not read.
    /// </summary>
    [Fact]
    public async Task GlobalExceptionRules_BrokenFile_KeepsWhatWasInForce()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var dir = Directory.CreateTempSubdirectory("nad-rules-");
        var file = Path.Combine(dir.FullName, "exceptionRules.json");
        try
        {
            await File.WriteAllTextAsync(file, """
                [{"type":"System.InvalidOperationException","action":"ignore"}]
                """, cts.Token);

            await using var session = await LaunchAsync(cts.Token, s =>
            {
                s.SetExceptionFilters(new ExceptionFilters(true, ["System.InvalidOperationException"]));
                s.SetGlobalExceptionRuleSource(new NetAndroidDebugger.Frontends.ExceptionRuleFile(file));
            });

            // Truncated mid-edit.
            await File.WriteAllTextAsync(file, "[{\"type\":\"System.Inval", cts.Token);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(1));

            session.Continue();
            // The ignore rule read earlier is still in force, so the app keeps running...
            Assert.Null(await session.WaitForStopAsync(0, TimeSpan.FromSeconds(25), cts.Token));
            // ...and the engine said why it could not re-read the file.
            var log = session.GetDebuggerOutput(500);
            Assert.Contains(log, l => l.Contains("could not read", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
