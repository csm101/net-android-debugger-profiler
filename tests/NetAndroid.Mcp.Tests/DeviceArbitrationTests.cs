using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

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

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NetAndroidDebuggerProfiler.slnx")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
        }
    }

    private static string UnifiedServerDll => Path.Combine(RepoRoot, "src", "NetAndroid.Mcp", "bin",
#if DEBUG
        "Debug",
#else
        "Release",
#endif
        "net10.0", "NetAndroid.Mcp.dll");

    private static async Task<string?> DeviceSerialAsync(AdbClient adb, CancellationToken ct)
    {
        foreach (var variable in new[] { "NAD_DEVICE_SERIAL", "NAP_TEST_SERIAL" })
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } serial)
                return serial;
        var online = (await adb.ListDevicesAsync(ct)).Where(d => d.State == "device").ToList();
        return online.Count == 1 ? online[0].Serial : null;
    }

    private static async Task<(bool IsError, string Text)> CallAsync(McpClient client, string tool, Dictionary<string, object?> args, CancellationToken ct)
    {
        var result = await client.CallToolAsync(tool, args, cancellationToken: ct);
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        return (result.IsError == true, text);
    }

    [SkippableFact]
    public async Task ProfileRun_WhileTheDebuggerHoldsTheDevice_IsRefused_AndNotAfterStopDebugging()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;
        var adb = new AdbClient();
        var serial = await DeviceSerialAsync(adb, ct);
        Skip.If(serial is null, "no device: set NAD_DEVICE_SERIAL or NAP_TEST_SERIAL, or attach exactly one");
        Skip.If((await adb.PackagePathsAsync(serial!, DebuggerTestTarget, ct)).Count == 0,
            $"{DebuggerTestTarget} is not installed on {serial}: run the debugger's suite once, it deploys it");

        var transport = new StdioClientTransport(new StdioClientTransportOptions { Name = "net-android", Command = "dotnet", Arguments = [UnifiedServerDll] });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
        var profileArgs = new Dictionary<string, object?> { ["deviceSerial"] = serial, ["packageName"] = DebuggerTestTarget, ["durationSeconds"] = 2 };
        try
        {
            var launch = await CallAsync(client, "launch_app", new() { ["deviceSerial"] = serial, ["packageName"] = DebuggerTestTarget }, ct);
            Assert.False(launch.IsError, launch.Text);

            var refused = await CallAsync(client, "profile_run", profileArgs, ct);
            Assert.True(refused.IsError, refused.Text);
            Assert.Contains("stop_debugging", refused.Text);
            Assert.Contains(serial!, refused.Text);
        }
        finally
        {
            await CallAsync(client, "stop_debugging", new(), ct);
        }

        // Whatever the profiler now says about this app (built for debugging, not for profiling),
        // it is the profiler speaking, not the arbiter.
        var afterwards = await CallAsync(client, "profile_run", profileArgs, ct);
        Assert.DoesNotContain("stop_debugging", afterwards.Text);
    }
}
