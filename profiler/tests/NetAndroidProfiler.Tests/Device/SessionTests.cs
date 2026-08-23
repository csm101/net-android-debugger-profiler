using NetAndroidProfiler.Core.Apps;
using NetAndroidProfiler.Core.Collection;
using NetAndroidProfiler.Core.Devices;
using NetAndroidProfiler.Core.Sessions;
using NetAndroidProfiler.Core.Store;

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

    [SkippableFact]
    public async Task Instrumenting_restart_session_times_methods()
    {
        // trackAllocations is off on purpose: this runtime serves allocations or
        // enter/leave, not both (see the characterization test below).
        //
        // The retry is not test hygiene, it is the subject being unreliable: this runtime
        // sometimes instruments nothing at all for a whole session, in runs, and recovers
        // by itself later (KNOWN_UNKNOWNS U23). One repeat separates "the runtime skipped
        // this session" from "our pipeline lost the events", and a second empty run still
        // fails, loudly, with the count.
        ProfilerSession s = await RunProviderInstrumentingAsync();
        var timings = s.Results.Timings(20);
        if (timings.Count == 0)
        {
            await s.DisposeAsync();
            s = await RunProviderInstrumentingAsync();
            timings = s.Results.Timings(20);
        }
        await using var session = s;
        Assert.Equal(SessionState.Ready, s.State);
        // Two empty sessions in a row is the runtime refusing to instrument, not our
        // pipeline losing events: the weaver tests in this class record hundreds of
        // thousands of enter/leave pairs from the same app minutes apart, and the traces
        // of the empty runs contain no MethodEnter when decoded by hand. Skipping says
        // "not measured today" rather than reporting a defect we did not find - and the
        // day the runtime is fixed, this stops skipping on its own.
        Skip.If(timings.Count == 0,
            "the runtime instrumented nothing in two consecutive sessions - KNOWN_UNKNOWNS U23");

        // Which methods a ten-second window catches depends on where the workload had got
        // to, so naming one of the deeper ones makes the test a report on the app's
        // scheduling rather than on the profiler. What must hold is that enter/leave really
        // arrived, that the durations are real, and that the callspec was honoured.
        Assert.NotEmpty(timings);
        Assert.All(timings, t => Assert.StartsWith("TestTarget.Workloads", t.FullName));
        Assert.Contains(timings, t => t.Calls >= 100 && t.TotalNs > 0);
        // The tree carries the same events: a method that was entered has a node.
        Assert.NotEmpty(s.Results.TimingTreeChildren(null));
    }

    private static Task<ProfilerSession> RunProviderInstrumentingAsync() =>
        RunAsync(new SessionSpec(Serial, Package, ProfilingMode.Instrumenting,
            Duration: TimeSpan.FromSeconds(10),
            Callspec: "N:TestTarget.Workloads",
            TrackAllocations: false));

    /// <summary>
    /// One provider session carrying both timings and allocations - what the engine is for.
    ///
    /// This was briefly documented as impossible: a run of sessions measured on 2026-08-23
    /// produced allocations and not one MethodEnter, whatever the MONO_DIAGNOSTICS spelling.
    /// A restarted emulator produced both from the same code minutes later (50,504 calls of
    /// NewRecord with real durations), so the exclusivity was the device degrading, not the
    /// runtime's design - KNOWN_UNKNOWNS U23. The skip below is that degradation, and it
    /// stops as soon as the device is fresh.
    /// </summary>
    [SkippableFact]
    public async Task Instrumenting_provider_records_timings_and_allocations_together()
    {
        await using var s = await RunAsync(new SessionSpec(Serial, Package, ProfilingMode.Instrumenting,
            Duration: TimeSpan.FromSeconds(10),
            Callspec: "T:TestTarget.Workloads.AllocHog,T:TestTarget.Workloads.AllocHeavyRecord",
            TrackAllocations: true));
        Assert.Equal(SessionState.Ready, s.State);

        var allocs = s.Results.AllocationsByType(10);
        var timings = s.Results.Timings(20);
        Skip.If(timings.Count == 0,
            "the runtime stopped instrumenting methods on this device - restart it; KNOWN_UNKNOWNS U23");

        Assert.NotEmpty(allocs);
        var record = allocs.Single(a => a.TypeName == "TestTarget.Workloads.AllocHeavyRecord");
        var newRecord = timings.Single(t => t.FullName == "TestTarget.Workloads.AllocHog.NewRecord");
        Assert.True(newRecord.Calls > 0, "the woven method must carry calls");
        Assert.True(newRecord.TotalNs > 0, "and a duration");
        // The allocations cover the constructor calls they come from.
        Assert.True(record.Count >= newRecord.Calls,
            $"allocations {record.Count} should cover the {newRecord.Calls} constructor calls");
        Assert.Contains(s.Results.AllocationsBySite(10),
            a => a.MethodFullName.EndsWith("NewRecord"));
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

    /// <summary>
    /// A heap session with nothing configured - what `nap run --mode heap` and the MCP
    /// profile_run send. SuspendOnStart defaults to true and used to be honoured here,
    /// which held the runtime at startup: the app allocated nothing, the warm-up ticked
    /// against a frozen process and the session ended with "Heap snapshot produced no
    /// objects", however long the warm-up was. A heap session never suspends now.
    /// </summary>
    /// <summary>
    /// The third engine: the same weaving, with the app keeping a call tree instead of
    /// writing an event per call. The results have to be the same shape as the event
    /// stream's - calls, times, a tree with parents - because everything downstream reads
    /// them the same way; what differs is what it cost to get them.
    /// </summary>
    [Fact]
    public async Task Weaver_tree_engine_records_the_same_shape_for_a_fraction_of_the_data()
    {
        await using var s = await RunAsync(new SessionSpec(Serial, Package, ProfilingMode.Instrumenting,
            Duration: TimeSpan.FromSeconds(8),
            Callspec: "N:TestTarget.Workloads",
            Engine: InstrumentingEngine.WeaverTree,
            WeaveAssemblies: ["TestTarget"]));
        Assert.Equal(SessionState.Ready, s.State);

        var timings = s.Results.Timings(30);
        Assert.NotEmpty(timings);
        Assert.All(timings, t => Assert.StartsWith("TestTarget.Workloads", t.FullName));
        var mix = timings.Single(t => t.FullName == "TestTarget.Workloads.CpuBurner.Mix");
        Assert.True(mix.Calls > 100, $"Mix calls = {mix.Calls}");
        Assert.True(mix.TotalNs > 0 && mix.MinNs > 0 && mix.MaxNs >= mix.MinNs,
            "a node carries its own minimum and maximum");

        // The tree is the point: parents, children and depth, without an event per call.
        var roots = s.Results.TimingTreeChildren(null);
        Assert.NotEmpty(roots);
        Assert.Contains(roots, r => r.HasChildren);

        // And the app wrote nodes rather than a call-by-call stream. Allocations stay events,
        // so a small .napw is expected; what must not happen is megabytes of enter/leave.
        var written = Directory.GetFiles(Path.Combine(s.Directory, "events"));
        Assert.Contains(written, f => f.EndsWith(".napt", StringComparison.OrdinalIgnoreCase));
        long bytes = written.Sum(f => new FileInfo(f).Length);
        Assert.True(bytes < 256 * 1024,
            $"{mix.Calls} calls of Mix alone should not have produced {bytes} bytes");
    }

    /// <summary>
    /// A real app is several assemblies, and every layer of the profiler works per module:
    /// the weaver is told which assemblies to rewrite, methods are named per module, and
    /// symbolication looks up a pdb per module. One session must therefore weave both
    /// TestTarget and TestTarget.Support and give each a source location.
    /// </summary>
    [SkippableFact]
    public async Task One_session_symbolicates_methods_from_two_assemblies()
    {
        string symbols = BuildOutputDirectory();
        Skip.If(symbols is null, "TestTarget build output not found: build the app first");
        Skip.IfNot(File.Exists(Path.Combine(symbols!, "TestTarget.Support.pdb")),
            "the app was built before it had a second assembly: rebuild and reinstall TestTarget");

        // Woven rather than sampled: the support method runs in microseconds, so a sampler
        // would catch it only by luck, while enter/leave records every call. This also
        // covers weaving more than one assembly in a session, which is what a real app
        // needs (App.Droid plus App.Core, and so on).
        await using var s = await RunAsync(new SessionSpec(Serial, Package, ProfilingMode.Instrumenting,
            Duration: TimeSpan.FromSeconds(10),
            Callspec: "N:TestTarget.Workloads,N:TestTarget.Support",
            Engine: InstrumentingEngine.Weaver,
            WeaveAssemblies: ["TestTarget", "TestTarget.Support"],
            SymbolsDir: symbols));
        Assert.Equal(SessionState.Ready, s.State);

        var modules = s.Results.Methods()
            .Select(m => m.Module)
            .Where(m => m.StartsWith("TestTarget", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.Contains("TestTarget", modules, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("TestTarget.Support", modules, StringComparer.OrdinalIgnoreCase);

        // Both modules resolved to source, which is what the pdb lookup is for.
        foreach (var module in new[] { "TestTarget", "TestTarget.Support" })
        {
            var figures = s.Results.MethodFiguresByModule(module);
            Assert.True(figures.Count > 0, $"no methods recorded for {module}");
        }
        Assert.Contains(SourceFiles(s.DatabasePath), f => f.EndsWith("SupportWork.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(SourceFiles(s.DatabasePath), f => f.EndsWith("CpuBurner.cs", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Source files recorded for the session's methods.</summary>
    private static IEnumerable<string> SourceFiles(string databasePath)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT source_file FROM method WHERE source_file IS NOT NULL";
        using var reader = cmd.ExecuteReader();
        var files = new List<string>();
        while (reader.Read()) files.Add(reader.GetString(0));
        return files;
    }

    /// <summary>The app's build output, where its pdbs are.</summary>
    private static string? BuildOutputDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NetAndroidProfiler.slnx")))
            dir = dir.Parent;
        if (dir is null) return null;
        string bin = Path.Combine(dir.FullName, "TestTarget", "bin", "Debug");
        return Directory.Exists(bin)
            ? Directory.GetDirectories(bin).FirstOrDefault(d => File.Exists(Path.Combine(d, "TestTarget.pdb")))
            : null;
    }

    /// <summary>
    /// An app built without the diagnostics component cannot be profiled at all, and the
    /// refusal is the product for whoever hits it: it has to name the missing library and
    /// the build switch that puts it back, before anything is collected.
    ///
    /// Needs the companion build, installed beside the normal one so both can live on the
    /// device:
    ///   dotnet build TestTarget/TestTarget.csproj -c Debug -p:EnableDiagnostics=false
    ///       -p:ApplicationId=com.mcasoftware.testtarget.nodiag -t:Install -p:AdbTarget="-s emulator-5556"
    /// </summary>
    [SkippableFact]
    public async Task An_app_built_without_diagnostics_is_refused_with_guidance()
    {
        const string package = "com.mcasoftware.testtarget.nodiag";
        var adb = new AdbClient();
        string installed = await adb.ShellAsync(Serial, "pm list packages " + package, CancellationToken.None);
        Skip.IfNot(installed.Contains(package, StringComparison.Ordinal),
            $"{package} is not installed - see this test's summary for the build command");

        var session = ProfilerSession.Create(new SessionSpec(Serial, package, ProfilingMode.Sampling,
            Duration: TimeSpan.FromSeconds(5)), SessionsRoot);
        await using var _ = session;
        var error = await Assert.ThrowsAnyAsync<Exception>(() => session.RunAsync(CancellationToken.None));

        Assert.Contains("libmono-component-diagnostics_tracing.so", error.Message);
        Assert.Contains("-p:EnableDiagnostics=true", error.Message);
        Assert.Equal(SessionState.Failed, session.State);
        // Refused before collecting: no trace was written.
        Assert.False(File.Exists(Path.Combine(session.Directory, "trace.nettrace")));
    }

    /// <summary>
    /// A session with no duration collects until somebody stops it - what the GUI's Stop
    /// button and the MCP profile_stop do. The engine has to end collection, analyse what
    /// it has and reach Ready, rather than wait for a duration that will never come.
    /// </summary>
    [Fact]
    public async Task Stop_ends_a_session_that_was_started_without_a_duration()
    {
        var session = ProfilerSession.Create(
            new SessionSpec(Serial, Package, ProfilingMode.Sampling), SessionsRoot);   // no Duration
        var run = Task.Run(() => session.RunAsync(CancellationToken.None));
        try
        {
            await WaitForStateAsync(session, SessionState.Collecting, TimeSpan.FromMinutes(2));
            await Task.Delay(TimeSpan.FromSeconds(6));

            session.Stop();
            var info = await run.WaitAsync(TimeSpan.FromMinutes(3));

            Assert.Equal(SessionState.Ready, info.State);
            using var store = ResultStore.Open(session.DatabasePath);
            var row = store.ReadSession();
            Assert.NotNull(row);
            Assert.True(row!.TotalSamples > 0, "a stopped session must carry the samples it collected");
            Assert.NotEmpty(store.SampleTreeChildren(null));
        }
        finally
        {
            session.Stop();
            await session.DisposeAsync();
        }
    }

    /// <summary>
    /// Startup profiling: with suspend, the session is up before the app runs a line, so
    /// the tree carries the activity's OnCreate. Without it the app would be past its
    /// initialisation by the time the first sample lands - which is the whole reason the
    /// launch is suspended.
    /// </summary>
    [Fact]
    public async Task Suspended_start_captures_the_app_initialisation()
    {
        await using var s = await RunAsync(new SessionSpec(Serial, Package, ProfilingMode.Sampling,
            Duration: TimeSpan.FromSeconds(10), SuspendOnStart: true));
        Assert.Equal(SessionState.Ready, s.State);

        var names = AllTreeMethods(s.Results).ToList();
        Assert.Contains(names, n => n.Contains("MainActivity", StringComparison.Ordinal));
        Assert.Contains(names, n => n.Contains("OnCreate", StringComparison.Ordinal));
    }

    /// <summary>Every method named anywhere in the sample tree, depth first.</summary>
    private static IEnumerable<string> AllTreeMethods(ResultStore results)
    {
        var pending = new Stack<int?>();
        pending.Push(null);
        while (pending.Count > 0)
        {
            foreach (var row in results.SampleTreeChildren(pending.Pop(), top: 200))
            {
                yield return row.FullName;
                if (row.HasChildren) pending.Push(row.Id);
            }
        }
    }

    [Fact]
    public async Task Heap_snapshot_with_default_settings_captures_objects()
    {
        await using var s = await RunAsync(new SessionSpec(Serial, Package, ProfilingMode.HeapSnapshot));
        Assert.Equal(SessionState.Ready, s.State);
        var byType = s.Results.HeapByType(1, 20);
        Assert.NotEmpty(byType);
        Assert.Contains(byType, r => r.count > 0);
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

    /// <summary>
    /// U5: a trace grows for as long as the session runs, and an instrumenting session on a
    /// busy callspec grows fastest. The size limit must end collection cleanly - the part
    /// already collected stays a valid, analyzable trace - and say so in the session warnings
    /// rather than failing or filling the disk.
    /// </summary>
    [SkippableFact]
    public async Task Collection_stops_when_the_trace_reaches_its_size_limit()
    {
        const long limit = 2 * 1024 * 1024;
        await using var s = await RunAsync(new SessionSpec(Serial, Package, ProfilingMode.Instrumenting,
            Duration: TimeSpan.FromMinutes(2),                 // far longer than the limit needs
            Callspec: "N:TestTarget.Workloads",
            MaxTraceBytes: limit));
        Assert.Equal(SessionState.Ready, s.State);

        // The trace only grows if the runtime is instrumenting, so on a device that has
        // stopped doing that (KNOWN_UNKNOWNS U23) this measures the device, not the size
        // limit. Skipping says so instead of blaming the limit.
        var trace = new FileInfo(Path.Combine(s.Directory, "trace.nettrace"));
        Skip.If(s.Results.Timings(1).Count == 0 && trace.Length < limit,
            "the runtime instrumented nothing, so the trace never grew - restart the device; KNOWN_UNKNOWNS U23");

        Assert.True(trace.Exists && trace.Length >= limit, $"trace is {trace.Length} bytes");
        // The check runs every 250 ms, so the file overshoots a little - but nowhere near
        // what two minutes of collection would have produced.
        Assert.True(trace.Length < limit * 8, $"trace overshot the limit: {trace.Length} bytes");
        Assert.Contains(s.Info.Warnings, w => w.Contains("limit", StringComparison.OrdinalIgnoreCase));
        Assert.True((s.Info.EndedUtc - s.Info.StartedUtc)!.Value < TimeSpan.FromMinutes(2), "the size limit, not the duration, must have ended it");
        // What was collected is still a usable profile: stopping the session cleanly makes
        // the runtime emit its rundown, so method names still resolve.
        var timings = s.Results.Timings(10);
        Assert.NotEmpty(timings);
        Assert.Contains(timings, t => t.FullName.StartsWith("TestTarget.Workloads", StringComparison.Ordinal));
    }

    /// <summary>
    /// U7's live control on the engine that can serve it: snapshot refreshes the results
    /// while the app keeps running, pause stops the events without stopping the app, and
    /// clear throws away what was collected. This is AQTime's Get Results / Disable
    /// Profiling / Clear Results, and it is what the GUI's toolbar drives.
    /// </summary>
    [Fact]
    public async Task Weaver_session_can_snapshot_pause_and_clear_while_the_app_runs()
    {
        var session = ProfilerSession.Create(new SessionSpec(Serial, Package, ProfilingMode.Instrumenting,
            Callspec: "N:TestTarget.Workloads",
            Engine: InstrumentingEngine.Weaver,
            WeaveAssemblies: ["TestTarget"]), SessionsRoot);          // no duration: runs until Stop()
        var run = Task.Run(() => session.RunAsync(CancellationToken.None));
        try
        {
            await WaitForStateAsync(session, SessionState.Collecting, TimeSpan.FromMinutes(3));
            await Task.Delay(TimeSpan.FromSeconds(4));

            int first = await session.SnapshotAsync();
            long callsFirst = TotalCalls(session.DatabasePath);
            Assert.True(callsFirst > 0, "the first snapshot saw no calls");

            await Task.Delay(TimeSpan.FromSeconds(3));
            await session.SnapshotAsync();
            long callsSecond = TotalCalls(session.DatabasePath);
            Assert.True(callsSecond > callsFirst, $"calls did not grow between snapshots: {callsFirst} -> {callsSecond}");

            // Pause: the collector polls its control file once a second, so let it settle,
            // then check that two snapshots three seconds apart see the same numbers.
            await session.PauseAsync();
            await Task.Delay(TimeSpan.FromSeconds(3));
            await session.SnapshotAsync();
            long paused = TotalCalls(session.DatabasePath);
            await Task.Delay(TimeSpan.FromSeconds(3));
            await session.SnapshotAsync();
            Assert.Equal(paused, TotalCalls(session.DatabasePath));

            await session.ResumeAsync();
            await Task.Delay(TimeSpan.FromSeconds(3));
            await session.SnapshotAsync();
            Assert.True(TotalCalls(session.DatabasePath) > paused, "resume did not restart collection");

            await session.ClearAsync();
            Assert.Equal(0, TotalCalls(session.DatabasePath));

            // After a clear the collector must start writing new files: if it kept its old
            // handles the app would be filling files that no longer have a name, and the
            // next analysis would pair leaves with enters from another era - which shows up
            // as negative durations.
            await Task.Delay(TimeSpan.FromSeconds(5));
            await session.SnapshotAsync();
            Assert.True(TotalCalls(session.DatabasePath) > 0, "collection did not restart after clear");
            using (var afterClear = ResultStore.Open(session.DatabasePath))
                Assert.All(afterClear.Timings(200), t =>
                {
                    Assert.True(t.TotalNs >= 0, $"{t.FullName} has a negative total time");
                    Assert.True(t.SelfNs >= 0, $"{t.FullName} has a negative self time");
                });

            // The history says what happened, and the app was never restarted.
            using var store = ResultStore.Open(session.DatabasePath);
            var segments = store.Segments();
            Assert.True(segments.Count(s => s.kind == "snapshot") >= 5, $"segments: {segments.Count}");
            Assert.Contains(segments, s => s.kind == "clear");
        }
        finally
        {
            session.Stop();
            try { await run; } catch { /* the assertions above own the failure */ }
            await session.DisposeAsync();
        }
    }

    private static long TotalCalls(string databasePath)
    {
        using var store = ResultStore.Open(databasePath);
        return store.Timings(500).Sum(t => t.Calls);
    }

    private static async Task WaitForStateAsync(ProfilerSession session, SessionState state, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (session.State == state) return;
            if (session.State is SessionState.Failed)
                throw new Xunit.Sdk.XunitException($"session failed before reaching {state}: {session.Info.Error}");
            await Task.Delay(250);
        }
        throw new Xunit.Sdk.XunitException($"session stayed {session.State}, never reached {state}");
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
