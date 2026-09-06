using Xunit.Abstractions;

namespace NetAndroid.Device.Tests;

/// <summary>
/// The unified client against a real device: what both products rely on from the very first call.
/// The listing carries every field either product had (the debugger's four, the profiler's API level,
/// ABI and AVD name), and a failed command surfaces as the exception hierarchy both products catch.
/// </summary>
[Trait("Category", "Device")]
[Collection(DeviceCollection.Name)]
public sealed class AdbClientTests(DeviceFixture device, ITestOutputHelper output)
{
    [Fact]
    public async Task ListDevices_DescribesAnOnlineDevice_WithEveryFieldEitherProductHad()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var devices = await device.Adb.ListDevicesAsync(cts.Token);
        var ours = Assert.Single(devices, d => d.Serial == device.Serial);
        output.WriteLine(ours.ToString());

        Assert.Equal("device", ours.State);
        Assert.False(string.IsNullOrWhiteSpace(ours.Model));
        Assert.True(ours.ApiLevel > 0, "the API level comes from ro.build.version.sdk");
        Assert.False(string.IsNullOrWhiteSpace(ours.Abi), "the ABI names the folder the app's override environment lives under");
        Assert.Equal(device.Serial.StartsWith("emulator-", StringComparison.Ordinal), ours.IsEmulator);
        if (ours.IsEmulator) Assert.False(string.IsNullOrWhiteSpace(ours.AvdName));
    }

    [Fact]
    public async Task AFailedCommand_IsAnAdbException_AndAToolException()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var ex = await Assert.ThrowsAsync<AdbException>(() => device.Adb.RunDeviceAsync(device.Serial, ["shell", "exit 3"], cts.Token));
        Assert.IsAssignableFrom<ToolException>(ex);
        Assert.Contains("failed (3)", ex.Message);
    }

    [Fact]
    public async Task Shell_ReturnsTrimmedOutput_AndGetPropOfAnUnsetProperty_IsEmpty()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        Assert.Equal("hello", await device.Adb.ShellAsync(device.Serial, "echo hello", cts.Token));
        Assert.Equal("", await device.Adb.GetPropAsync(device.Serial, "debug.netandroid.device.tests.unset", cts.Token));
    }
}
