using NetAndroidProfiler.Core.Apps;
using NetAndroidProfiler.Core.Collection;
using NetAndroidProfiler.Core.Devices;
using NetAndroidProfiler.Core.Sessions;

namespace NetAndroidProfiler.Tests.Device;

/// <summary>
/// End-to-end sessions against TestTarget on the profiler emulator. Needs
/// TestTarget installed as a Debug build with EnableDiagnostics=true:
///   dotnet build TestTarget/TestTarget.csproj -c Debug -p:EnableDiagnostics=true -t:Install -p:AdbTarget="-s emulator-5556"
/// Serial/package can be overridden with NAP_TEST_SERIAL / NAP_TEST_PACKAGE.
/// </summary>
[Trait("Category", "Device")]
[Collection("device")]
public class SessionTests
{
    private static string Serial => Environment.GetEnvironmentVariable("NAP_TEST_SERIAL") ?? "emulator-5556";
    private static string Package => Environment.GetEnvironmentVariable("NAP_TEST_PACKAGE") ?? "com.mcasoftware.testtarget";
    private static string SessionsRoot => Path.Combine(Path.GetTempPath(), "net-android-profiler-tests", "sessions");

    private static async Task<ProfilerSession> RunAsync(SessionSpec spec)
    {
        var session = ProfilerSession.Create(spec, SessionsRoot);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try
        {
            await session.RunAsync(cts.Token);
        }
        catch
        {
            Console.WriteLine(string.Join(Environment.NewLine, session.LogLines));
            throw;
        }
        return session;
    }

    /// <summary>
    /// Start the app the way a user profiling with Attach must have started it: with a
    /// diagnostics port in its environment, since nothing the session does later can give
    /// a port to a process that is already running. The app dials the address until the
    /// profiler's dsrouter answers, so it may be started before the session exists.
    /// Returns the environment handle; the caller restores it and stops the app, whose
    /// process keeps the port in its environment and would otherwise reconnect to the next
    /// session's dsrouter and be profiled in place of the intended app.
    /// </summary>
    private static async Task<AppEnvironment> StartAppWithDiagnosticPortAsync(AdbClient adb, CancellationToken ct)
    {
        var device = (await adb.ListDevicesAsync(ct)).Single(d => d.Serial == Serial);
        var env = new AppEnvironment(adb, Serial, Package, device.Abi);
        string address = device.IsEmulator ? "10.0.2.2" : "127.0.0.1";
        await adb.ForceStopAsync(Serial, Package, ct);
        await env.ApplyOverrideAsync([new("DOTNET_DiagnosticPorts", $"{address}:{DsRouterProcess.AppPort},nosuspend,connect")], ct);
        await adb.LaunchAsync(Serial, Package, ct);
        await Task.Delay(TimeSpan.FromSeconds(8), ct);
        return env;
    }

    [Fact]
    public async Task Sampling_restart_session_finds_busy_method()
    {
        await using var s = await RunAsync(new SessionSpec(Serial, Package, ProfilingMode.Sampling, Duration: TimeSpan.FromSeconds(12)));
        Assert.Equal(SessionState.Ready, s.State);
        var hot = s.Results.Hotspots(5, exclusive: true, cpuOnly: true);
        Assert.Contains(hot, h => h.FullName.Contains("CpuBurner.Busy"));
        Assert.True(s.Results.ReadSession()!.TotalSamples > 500);
        Assert.True(File.Exists(Path.Combine(s.Directory, "trace.nettrace")));
    }

    [Fact]
    public async Task Instrumenting_restart_session_times_methods_and_counts_allocations()
    {
        await using var s = await RunAsync(new SessionSpec(Serial, Package, ProfilingMode.Instrumenting,
            Duration: TimeSpan.FromSeconds(10),
            Callspec: "M:TestTarget.Workloads.CpuBurner:Busy,T:TestTarget.Workloads.AllocHog,T:TestTarget.Workloads.AllocHeavyRecord,T:TestTarget.Workloads.WorkloadRunner",
            TrackAllocations: true));
        Assert.Equal(SessionState.Ready, s.State);
        // How many iterations fit in the window depends on the machine and on how much
        // of it the app spent starting, so the count itself is not the assertion: the
        // floor only says the enter/leave stream really arrived, and the checks below
        // carry the meaning (allocations cover the constructor calls, the site is
        // attributed, an unmatched method stays out).
        var timings = s.Results.Timings(20);
        var newRecord = timings.Single(t => t.FullName == "TestTarget.Workloads.AllocHog.NewRecord");
        Assert.True(newRecord.Calls >= 500, $"NewRecord calls = {newRecord.Calls}");
        Assert.True(newRecord.TotalNs > 0, "woven timings must carry a duration");
        Assert.DoesNotContain(timings, t => t.FullName.Contains("CpuBurner.Mix"));
        var allocs = s.Results.AllocationsByType(10);
        var record = allocs.Single(a => a.TypeName == "TestTarget.Workloads.AllocHeavyRecord");
        Assert.True(record.Count >= newRecord.Calls, $"allocations {record.Count} should cover the {newRecord.Calls} constructor calls");
        var sites = s.Results.AllocationsBySite(10);
        Assert.Contains(sites, a => a.TypeName == "TestTarget.Workloads.AllocHeavyRecord" && a.MethodFullName.EndsWith("NewRecord"));
    }

    /// <summary>
    /// An app can be left with an empty environment override file - a write interrupted
    /// by a crash, an emulator that dies mid-session. The engine used to treat that as
    /// "exists but could not be read" and refuse every later session against the app,
    /// with no way out that did not involve knowing about the file. An empty file simply
    /// carries no variables.
    /// </summary>
    [Fact]
    public async Task Session_runs_when_the_app_has_an_empty_override_environment_file()
    {
        var adb = new AdbClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var device = (await adb.ListDevicesAsync(cts.Token)).Single(d => d.Serial == Serial);
        string dir = $"files/.__override__/{device.Abi}";
        await adb.RunAsAsync(Serial, Package, $"mkdir -p {dir} && rm -f {dir}/environment && touch {dir}/environment", cts.Token);
        Assert.Equal("0", await adb.RunAsAsync(Serial, Package, $"stat -c %s {dir}/environment", cts.Token));

        await using var s = await RunAsync(new SessionSpec(Serial, Package, ProfilingMode.Sampling, Duration: TimeSpan.FromSeconds(8)));
        Assert.Equal(SessionState.Ready, s.State);
        Assert.True(s.Results.ReadSession()!.TotalSamples > 0);
    }

    [Fact]
    public async Task Heap_snapshot_of_running_app_shows_retained_records()
    {
        // The app must already be running, with a port, and have filled its live set.
        var adb = new AdbClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var env = await StartAppWithDiagnosticPortAsync(adb, cts.Token);
        try
        {
            await using var s = await RunAsync(new SessionSpec(Serial, Package, ProfilingMode.HeapSnapshot, Launch: LaunchMode.Attach));
            Assert.Equal(SessionState.Ready, s.State);
            var rows = s.Results.HeapByType(1, 20);
            Assert.Contains(rows, r => r.typeName == "TestTarget.Workloads.AllocHeavyRecord" && r.count >= 1000);
            Assert.True((s.Info.EndedUtc - s.Info.StartedUtc)!.Value < TimeSpan.FromSeconds(60), "heap snapshot should end by quiescence, not by timeout");
        }
        finally
        {
            await env.RestoreAsync(CancellationToken.None);
            await adb.ForceStopAsync(Serial, Package, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Heap_snapshot_with_restart_warms_up_before_dumping()
    {
        await using var s = await RunAsync(new SessionSpec(Serial, Package, ProfilingMode.HeapSnapshot, Launch: LaunchMode.Restart, SuspendOnStart: false, Duration: TimeSpan.FromSeconds(6)));
        Assert.Equal(SessionState.Ready, s.State);
        Assert.Contains(s.Results.HeapByType(1, 20), r => r.typeName == "TestTarget.Workloads.AllocHeavyRecord");
    }

    [Fact]
    public async Task Two_heap_snapshots_support_a_growth_diff()
    {
        var adb = new AdbClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var env = await StartAppWithDiagnosticPortAsync(adb, cts.Token);
        try
        {
            await using var s = await RunAsync(new SessionSpec(Serial, Package, ProfilingMode.HeapSnapshot,
                Launch: LaunchMode.Attach, SnapshotCount: 2, SnapshotInterval: TimeSpan.FromSeconds(5)));
            Assert.Equal(SessionState.Ready, s.State);

            var snapshots = s.Results.HeapSnapshots();
            Assert.Equal(2, snapshots.Count);
            Assert.All(snapshots, x => Assert.True(x.objects > 1000));

            var diff = s.Results.HeapDiff(snapshots[0].id, snapshots[1].id, 20);
            Assert.NotEmpty(diff);
            // The retained record set is bounded, so it must be present in both snapshots.
            var record = diff.SingleOrDefault(d => d.TypeName == "TestTarget.Workloads.AllocHeavyRecord");
            Assert.NotNull(record);
            Assert.True(record!.CountFrom > 1000 && record.CountTo > 1000, $"{record.CountFrom} -> {record.CountTo}");
        }
        finally
        {
            await env.RestoreAsync(CancellationToken.None);
            await adb.ForceStopAsync(Serial, Package, CancellationToken.None);
        }
    }

    /// <summary>
    /// U15: does the sampler attribute samples to the true leaf method? The probe runs
    /// one long-bodied leaf and one tiny leaf called in a tight loop, with comparable
    /// CPU cost. The long leaf must show exclusive samples; what happens to the tiny
    /// one characterizes the sampler and is reported, not asserted.
    /// </summary>
    [Fact]
    public async Task Sampling_attributes_a_long_running_leaf_method()
    {
        await using var s = await RunAsync(new SessionSpec(Serial, Package, ProfilingMode.Sampling, Duration: TimeSpan.FromSeconds(15)));
        Assert.Equal(SessionState.Ready, s.State);
        var hot = s.Results.Hotspots(40, exclusive: true, cpuOnly: true);
        foreach (var h in hot.Where(h => h.FullName.Contains("LeafProbe")))
            Console.WriteLine($"U15 {h.Exclusive,6} excl {h.Inclusive,6} incl  {h.FullName}");

        var longLeaf = hot.SingleOrDefault(h => h.FullName.Contains("LeafProbe.LongLeaf"));
        Assert.NotNull(longLeaf);
        Assert.True(longLeaf!.ExclusiveCpu > 0, "a long-bodied leaf must own exclusive samples");

        var caller = hot.SingleOrDefault(h => h.FullName.Contains("LeafProbe.CallTinyLeaf"));
        var tiny = hot.SingleOrDefault(h => h.FullName.Contains("LeafProbe.TinyLeaf"));
        Console.WriteLine($"U15 tiny-leaf visible: {tiny is not null} (caller visible: {caller is not null})");
        Assert.NotNull(caller); // the loop that calls the tiny leaf must be attributed somewhere
    }

    [Fact]
    public async Task Sampling_attach_to_running_debug_app_without_restart()
    {
        var adb = new AdbClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var env = await StartAppWithDiagnosticPortAsync(adb, cts.Token);
        try
        {
            int pidBefore = (await adb.PidOfAsync(Serial, Package, cts.Token))!.Value;

            await using var s = await RunAsync(new SessionSpec(Serial, Package, ProfilingMode.Sampling, Launch: LaunchMode.Attach, Duration: TimeSpan.FromSeconds(10)));
            Assert.Equal(SessionState.Ready, s.State);
            int pidAfter = (await adb.PidOfAsync(Serial, Package, cts.Token))!.Value;
            Assert.Equal(pidBefore, pidAfter);
            Assert.Contains(s.Results.Hotspots(5), h => h.FullName.Contains("CpuBurner.Busy"));
        }
        finally
        {
            await env.RestoreAsync(CancellationToken.None);
            await adb.ForceStopAsync(Serial, Package, CancellationToken.None);
        }
    }

    /// <summary>
    /// U8, iterator half: an iterator's stub only builds the enumerator, so without weaving
    /// its MoveNext the method looks free. On the device the body must show one resumption
    /// per item produced.
    /// </summary>
    [Fact]
    public async Task Weaver_instruments_iterator_bodies_on_the_device()
    {
        await using var s = await RunAsync(new SessionSpec(Serial, Package, ProfilingMode.Instrumenting,
            Duration: TimeSpan.FromSeconds(8),
            Callspec: "T:TestTarget.Workloads.SequenceProducer",
            Engine: InstrumentingEngine.Weaver,
            WeaveAssemblies: ["TestTarget"]));
        Assert.Equal(SessionState.Ready, s.State);

        var timings = s.Results.Timings(20);
        Console.WriteLine(string.Join(Environment.NewLine, timings.Select(t => $"{t.Calls,6} {t.TotalNs / 1e6,9:F2}ms {t.SelfNs / 1e6,9:F2} self  {t.FullName}")));
        var body = timings.Single(t => t.FullName == "TestTarget.Workloads.SequenceProducer.Fibonacci (iterator body)");
        var consume = timings.Single(t => t.FullName == "TestTarget.Workloads.SequenceProducer.Consume");

        // The workload consumes the whole sequence, so each Consume drives ItemsPerIteration
        // resumptions plus the one that ends it. Machine speed decides how many iterations
        // fit in the window, so assert the ratio, not a count.
        Assert.True(consume.Calls >= 1, $"Consume calls = {consume.Calls}");
        Assert.True(body.Calls >= consume.Calls * 25, $"{body.Calls} resumptions for {consume.Calls} calls");
        Assert.True(body.TotalNs > 0);
        // The stub is a separate entry and does almost nothing itself.
        Assert.Contains(timings, t => t.FullName == "TestTarget.Workloads.SequenceProducer.Fibonacci");
    }

    [Fact]
    public async Task Weaver_instrumenting_session_times_woven_methods()
    {
        await using var s = await RunAsync(new SessionSpec(Serial, Package, ProfilingMode.Instrumenting,
            Duration: TimeSpan.FromSeconds(8),
            Callspec: "N:TestTarget.Workloads",
            Engine: InstrumentingEngine.Weaver,
            WeaveAssemblies: ["TestTarget"]));
        Assert.Equal(SessionState.Ready, s.State);
        var timings = s.Results.Timings(30);
        // Mix is called in a tight loop; instrumented it is slow, so the count is
        // far below the uninstrumented 2M/iteration but must be clearly non-trivial.
        var mix = timings.Single(t => t.FullName == "TestTarget.Workloads.CpuBurner.Mix");
        Assert.True(mix.Calls > 100, $"Mix calls = {mix.Calls}");
        Assert.True(mix.TotalNs > 0);
        Assert.All(timings, t => Assert.StartsWith("TestTarget.Workloads", t.FullName));
        // Only methods that returned within the window appear (Loop never returns,
        // Busy is still spinning): Mix and the constructors are enough to prove the
        // weaver recorded real enter/leave pairs from the woven app assembly.
        Assert.Contains(timings, t => t.FullName.EndsWith("..ctor"));
        var tree = s.Results.TimingTreeChildren(null);
        Assert.NotEmpty(tree);

        // trackAllocations is on by default: the weaver reports what the woven methods
        // allocate. Which types show up depends on how far the instrumented workload gets
        // inside the window (Mix dominates), so assert that allocations were recorded and
        // that their type names resolved through the collector's types file.
        var allocs = s.Results.AllocationsByType(20);
        Assert.NotEmpty(allocs);
        Assert.Contains(allocs, a => !a.TypeName.StartsWith("<type "));
        var sites = s.Results.AllocationsBySite(20);
        Assert.Contains(sites, a => a.MethodFullName.StartsWith("TestTarget.Workloads"));
    }

    /// <summary>
    /// Build-time weaving (U22): the app was woven by the build
    /// (-p:NapWeave=true -p:NapCallspec=...), so the session only consumes the map
    /// the build wrote and never rewrites anything on the device. Requires TestTarget
    /// to have been installed from such a build; NAP_WEAVE_MAP overrides the path.
    /// </summary>
    [SkippableFact]
    public async Task Build_time_weaving_session_uses_the_build_map()
    {
        string map = Environment.GetEnvironmentVariable("NAP_WEAVE_MAP")
            ?? Path.Combine(RepoRoot(), "TestTarget", "bin", "Debug", "net10.0-android", "nap-weave.map");
        Skip.IfNot(File.Exists(map), $"build TestTarget with -p:NapWeave=true first (no map at {map})");

        await using var s = await RunAsync(new SessionSpec(Serial, Package, ProfilingMode.Instrumenting,
            Duration: TimeSpan.FromSeconds(8),
            Engine: InstrumentingEngine.Weaver,
            WeaveMapPath: map));
        Assert.Equal(SessionState.Ready, s.State);
        var timings = s.Results.Timings(20);
        Assert.NotEmpty(timings);
        Assert.All(timings, t => Assert.StartsWith("TestTarget.Workloads", t.FullName));
        Assert.Contains(timings, t => t.FullName == "TestTarget.Workloads.CpuBurner.Mix");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NetAndroidProfiler.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    [Fact]
    public async Task Session_restores_app_environment_and_leaves_no_dsrouter()
    {
        var adb = new AdbClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var dev = (await adb.ListDevicesAsync(cts.Token)).Single(d => d.Serial == Serial);
        var env = new NetAndroidProfiler.Core.Collection.AppEnvironment(adb, Serial, Package, dev.Abi);
        var before = await env.ReadOverrideAsync(cts.Token);

        await using (var s = await RunAsync(new SessionSpec(Serial, Package, ProfilingMode.Instrumenting, Duration: TimeSpan.FromSeconds(4), Callspec: "T:TestTarget.Workloads.WorkloadRunner")))
        {
            Assert.Equal(SessionState.Ready, s.State);
        }

        var after = await env.ReadOverrideAsync(cts.Token);
        Assert.Equal(before.Select(v => $"{v.Key}={v.Value}"), after.Select(v => $"{v.Key}={v.Value}"));
        Assert.DoesNotContain(after, v => v.Key == "MONO_DIAGNOSTICS");
        Assert.Empty(System.Diagnostics.Process.GetProcessesByName("dotnet-dsrouter"));
    }

    [Fact]
    public async Task Missing_package_fails_with_guidance()
    {
        var session = ProfilerSession.Create(new SessionSpec(Serial, "com.example.does.not.exist", ProfilingMode.Sampling, Duration: TimeSpan.FromSeconds(1)), SessionsRoot);
        await using (session)
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => session.RunAsync(CancellationToken.None));
            Assert.Contains("not installed", ex.Message);
            Assert.Equal(SessionState.Failed, session.State);
        }
    }
}
