using System.Text.RegularExpressions;
using NetAndroidProfiler.Core.Collection;

namespace NetAndroid.Mcp.Tests;

/// <summary>
/// Debugging and profiling the same process in one session: run under the debugger to a
/// breakpoint at the threshold of the work, take the breakpoints away, attach the sampling
/// profiler to the very process, resume, and read the hotspots of what ran after the stop.
/// The soft debugger and the diagnostics port are separate parts of the Mono runtime; this
/// is the test that says whether they work together. Needs the profiler's TestTarget
/// installed, as the profiler's device suite leaves it.
/// </summary>
[Trait("Category", "Device")]
public sealed class DebugAndProfileTogetherTests
{
    private const string Package = "com.mcasoftware.testtarget";

    [SkippableFact]
    public async Task SamplingAttach_ToTheAppUnderTheDebugger_ProfilesWhatRunsAfterTheBreakpoint()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var ct = cts.Token;
        var adb = new AdbClient();
        var serial = await Support.DeviceSerialAsync(adb, ct);
        Skip.If(serial is null, "no device: set NAD_DEVICE_SERIAL or NAP_TEST_SERIAL, or attach exactly one");
        Skip.If((await adb.PackagePathsAsync(serial!, Package, ct)).Count == 0,
            $"{Package} is not installed on {serial}: run the profiler's device suite once, it deploys it");

        // An attach needs the app to carry its diagnostics port from the start, as a build with
        // EnableDiagnostics does. Writing it into the override environment makes the test
        // independent of how the installed APK was built.
        var device = (await adb.ListDevicesAsync(ct)).Single(d => d.Serial == serial);
        var env = new AppEnvironment(adb, serial!, Package, device.Abi);
        await adb.ForceStopAsync(serial!, Package, ct);
        var address = device.IsEmulator ? "10.0.2.2" : "127.0.0.1";
        await env.ApplyOverrideAsync([new("DOTNET_DiagnosticPorts", $"{address}:{DsRouterProcess.AppPort},nosuspend,connect")], ct);

        await using var client = await Support.ConnectAsync("NetAndroid.Mcp", ct);
        try
        {
            var source = Path.Combine(Support.RepoRoot, "TestTarget", "Profiler", "MainActivity.cs");
            var line = Support.LineOf(source, "_runner ??= WorkloadRunner.Start();");
            var bp = await Support.CallAsync(client, "set_breakpoint", new() { ["file"] = source, ["line"] = line }, ct);
            Assert.False(bp.IsError, bp.Text);
            var launch = await Support.CallAsync(client, "launch_app", new() { ["deviceSerial"] = serial, ["packageName"] = Package }, ct);
            Assert.False(launch.IsError, launch.Text);
            var stop = await Support.CallAsync(client, "wait_until_stopped", new() { ["timeoutSeconds"] = 60 }, ct);
            Assert.StartsWith("Stopped:", stop.Text);
            Assert.Contains($"MainActivity.cs:{line}", stop.Text);
            var pidAtBreakpoint = await adb.PidOfAsync(serial!, Package, ct);

            // What the agent does before handing the process to the profiler: nothing may stop it any more.
            var cleared = await Support.CallAsync(client, "remove_all_breakpoints", new(), ct);
            Assert.False(cleared.IsError, cleared.Text);
            var filters = await Support.CallAsync(client, "set_exception_filters", new() { ["firstChanceTypes"] = Array.Empty<string>() }, ct);
            Assert.False(filters.IsError, filters.Text);

            var started = await Support.CallAsync(client, "profile_start",
                new() { ["deviceSerial"] = serial, ["packageName"] = Package, ["launch"] = "attach" }, ct);
            Assert.False(started.IsError, started.Text);
            var sessionId = Regex.Match(started.Text, @"Started session (\S+)\.").Groups[1].Value;
            Assert.NotEmpty(sessionId);

            // Whether the runtime connects while the debugger holds it stopped is the empirical
            // half of this test: measured and reported, not asserted.
            var connectedWhileStopped = await ReachesCollectingAsync(client, sessionId, TimeSpan.FromSeconds(15), ct);
            Console.WriteLine($"profiler connected while the app was stopped at the breakpoint: {connectedWhileStopped}");

            var resumed = await Support.CallAsync(client, "continue_and_wait", new() { ["timeoutSeconds"] = 3 }, ct);
            Assert.Contains("timeout", resumed.Text, StringComparison.OrdinalIgnoreCase);
            Assert.True(await ReachesCollectingAsync(client, sessionId, TimeSpan.FromSeconds(45), ct),
                "the profiler never reached Collecting: " + (await Support.CallAsync(client, "profile_status", new() { ["sessionId"] = sessionId }, ct)).Text);
            await Task.Delay(TimeSpan.FromSeconds(8), ct);

            var stopped = await Support.CallAsync(client, "profile_stop", new() { ["sessionId"] = sessionId }, ct);
            Assert.False(stopped.IsError, stopped.Text);
            var hot = await Support.CallAsync(client, "profile_hotspots", new() { ["sessionId"] = sessionId, ["exclusive"] = true, ["cpuOnly"] = true }, ct);
            Assert.False(hot.IsError, hot.Text);
            Assert.Contains("CpuBurner.Busy", hot.Text);

            // The same process, still under the debugger: a breakpoint set now is hit again.
            Assert.Equal(pidAtBreakpoint, await adb.PidOfAsync(serial!, Package, ct));
            var busyLine = Support.LineOf(Path.Combine(Support.RepoRoot, "TestTarget", "Profiler", "Workloads", "CpuBurner.cs"), "Mix(");
            var again = await Support.CallAsync(client, "set_breakpoint",
                new() { ["file"] = Path.Combine(Support.RepoRoot, "TestTarget", "Profiler", "Workloads", "CpuBurner.cs"), ["line"] = busyLine }, ct);
            Assert.False(again.IsError, again.Text);
            var hit = await Support.CallAsync(client, "wait_until_stopped", new() { ["timeoutSeconds"] = 30 }, ct);
            Assert.StartsWith("Stopped:", hit.Text);
            Assert.Contains("CpuBurner", hit.Text);
        }
        finally
        {
            await Support.CallAsync(client, "stop_debugging", new(), CancellationToken.None);
            await env.RestoreAsync(CancellationToken.None);
            await adb.ForceStopAsync(serial!, Package, CancellationToken.None);
        }
    }

    private static async Task<bool> ReachesCollectingAsync(ModelContextProtocol.Client.McpClient client, string sessionId, TimeSpan within, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline)
        {
            var status = await Support.CallAsync(client, "profile_status", new() { ["sessionId"] = sessionId, ["logLines"] = 0 }, ct);
            if (status.Text.Contains("Collecting", StringComparison.Ordinal))
                return true;
            if (status.Text.Contains("Failed", StringComparison.Ordinal))
                return false;
            await Task.Delay(500, ct);
        }
        return false;
    }
}
