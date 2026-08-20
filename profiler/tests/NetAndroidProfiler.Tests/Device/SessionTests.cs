using NetAndroidProfiler.Core.Apps;
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
        var timings = s.Results.Timings(20);
        Assert.Contains(timings, t => t.FullName == "TestTarget.Workloads.AllocHog.NewRecord" && t.Calls >= 20000);
        Assert.DoesNotContain(timings, t => t.FullName.Contains("CpuBurner.Mix"));
        var allocs = s.Results.AllocationsByType(10);
        Assert.Contains(allocs, a => a.TypeName == "TestTarget.Workloads.AllocHeavyRecord" && a.Count >= 20000);
        var sites = s.Results.AllocationsBySite(10);
        Assert.Contains(sites, a => a.TypeName == "TestTarget.Workloads.AllocHeavyRecord" && a.MethodFullName.EndsWith("NewRecord"));
    }

    [Fact]
    public async Task Heap_snapshot_of_running_app_shows_retained_records()
    {
        // Make sure the app is running and has filled its live set.
        var adb = new AdbClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        if (await adb.PidOfAsync(Serial, Package, cts.Token) is null)
        {
            await adb.LaunchAsync(Serial, Package, cts.Token);
            await Task.Delay(TimeSpan.FromSeconds(8), cts.Token);
        }
        await using var s = await RunAsync(new SessionSpec(Serial, Package, ProfilingMode.HeapSnapshot, Launch: LaunchMode.Attach));
        Assert.Equal(SessionState.Ready, s.State);
        var rows = s.Results.HeapByType(1, 20);
        Assert.Contains(rows, r => r.typeName == "TestTarget.Workloads.AllocHeavyRecord" && r.count >= 1000);
        Assert.True((s.Info.EndedUtc - s.Info.StartedUtc)!.Value < TimeSpan.FromSeconds(60), "heap snapshot should end by quiescence, not by timeout");
    }

    [Fact]
    public async Task Heap_snapshot_with_restart_warms_up_before_dumping()
    {
        await using var s = await RunAsync(new SessionSpec(Serial, Package, ProfilingMode.HeapSnapshot, Launch: LaunchMode.Restart, SuspendOnStart: false, Duration: TimeSpan.FromSeconds(6)));
        Assert.Equal(SessionState.Ready, s.State);
        Assert.Contains(s.Results.HeapByType(1, 20), r => r.typeName == "TestTarget.Workloads.AllocHeavyRecord");
    }

    [Fact]
    public async Task Sampling_attach_to_running_debug_app_without_restart()
    {
        var adb = new AdbClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await adb.ForceStopAsync(Serial, Package, cts.Token);
        await adb.LaunchAsync(Serial, Package, cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
        int pidBefore = (await adb.PidOfAsync(Serial, Package, cts.Token))!.Value;

        await using var s = await RunAsync(new SessionSpec(Serial, Package, ProfilingMode.Sampling, Launch: LaunchMode.Attach, Duration: TimeSpan.FromSeconds(10)));
        Assert.Equal(SessionState.Ready, s.State);
        int pidAfter = (await adb.PidOfAsync(Serial, Package, cts.Token))!.Value;
        Assert.Equal(pidBefore, pidAfter);
        Assert.Contains(s.Results.Hotspots(5), h => h.FullName.Contains("CpuBurner.Busy"));
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
