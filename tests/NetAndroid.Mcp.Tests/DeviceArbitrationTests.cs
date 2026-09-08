namespace NetAndroid.Mcp.Tests;

/// <summary>
/// The arbiter against a real debug session, driven through the unified server on the device
/// <c>NAD_DEVICE_SERIAL</c> or <c>NAP_TEST_SERIAL</c> names (else the only one online). Needs the
/// debugger's TestTarget installed, as the debugger's suite leaves it.
/// </summary>
[Trait("Category", "Device")]
public sealed class DeviceArbitrationTests
{
    private const string DebuggerTestTarget = "net.androiddebugger.testtarget";

    [SkippableFact]
    public async Task ProfileRun_WhileTheDebuggerHoldsTheDevice_IsRefused_AndNotAfterStopDebugging()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;
        var adb = new AdbClient();
        var serial = await Support.DeviceSerialAsync(adb, ct);
        Skip.If(serial is null, "no device: set NAD_DEVICE_SERIAL or NAP_TEST_SERIAL, or attach exactly one");
        Skip.If((await adb.PackagePathsAsync(serial!, DebuggerTestTarget, ct)).Count == 0,
            $"{DebuggerTestTarget} is not installed on {serial}: run the debugger's suite once, it deploys it");

        await using var client = await Support.ConnectAsync("NetAndroid.Mcp", ct);
        var profileArgs = new Dictionary<string, object?> { ["deviceSerial"] = serial, ["packageName"] = DebuggerTestTarget, ["durationSeconds"] = 2 };
        try
        {
            var launch = await Support.CallAsync(client, "launch_app", new() { ["deviceSerial"] = serial, ["packageName"] = DebuggerTestTarget }, ct);
            Assert.False(launch.IsError, launch.Text);

            var refused = await Support.CallAsync(client, "profile_run", profileArgs, ct);
            Assert.True(refused.IsError, refused.Text);
            Assert.Contains("stop_debugging", refused.Text);
            Assert.Contains(serial!, refused.Text);
        }
        finally
        {
            await Support.CallAsync(client, "stop_debugging", new(), ct);
        }

        // Whatever the profiler now says about this app (built for debugging, not for profiling),
        // it is the profiler speaking, not the arbiter.
        var afterwards = await Support.CallAsync(client, "profile_run", profileArgs, ct);
        Assert.DoesNotContain("stop_debugging", afterwards.Text);
    }
}
