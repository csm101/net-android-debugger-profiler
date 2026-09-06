namespace NetAndroid.Device.Tests;

/// <summary>
/// Noticing that another tool already owns a device-global property, and giving it back. The
/// debugger's `debug.mono.extra` carries a deadline and is only a conflict while it is fresh; the
/// profiler's `debug.mono.profile` carries none, so any value there is a mark left by someone.
/// The pure cases mirror the debugger's DebugPropertyTests, which still run through the launcher.
/// </summary>
public sealed class DevicePropertyOverrideWarningTests
{
    private const long Now = 1_787_473_000;

    [Fact]
    public void NoValue_IsNothingToSay_ForEitherProperty()
    {
        foreach (var value in new[] { null, "", "   " })
        {
            Assert.Null(DevicePropertyOverride.ForeignValueWarning(DeviceGlobals.DebugMonoExtra, value, Now));
            Assert.Null(DevicePropertyOverride.ForeignValueWarning(DeviceGlobals.DebugMonoProfile, value, Now, deadlineRequired: false));
        }
    }

    [Fact]
    public void AnExpiredDebuggerProperty_IsNotAConflict()
    {
        var expired = $"debug=127.0.0.1:10000,timeout={Now - 1},loglevel=0,server=y";
        Assert.Null(DevicePropertyOverride.ForeignValueWarning(DeviceGlobals.DebugMonoExtra, expired, Now));
    }

    [Fact]
    public void AFreshDebuggerProperty_SaysWhichPortAndForHowLong()
    {
        var fresh = $"debug=127.0.0.1:10500,timeout={Now + 120},loglevel=0,server=y";
        var warning = DevicePropertyOverride.ForeignValueWarning(DeviceGlobals.DebugMonoExtra, fresh, Now);
        Assert.NotNull(warning);
        Assert.Contains(DeviceGlobals.DebugMonoExtra, warning);
        Assert.Contains("10500", warning);
        Assert.Contains("120s", warning);
        Assert.Contains("device-global", warning);
    }

    [Fact]
    public void ADebuggerPropertyWithoutAReadableDeadline_IsNotJudged()
    {
        Assert.Null(DevicePropertyOverride.ForeignValueWarning(DeviceGlobals.DebugMonoExtra, "debug=127.0.0.1:10000,server=y", Now));
        Assert.Null(DevicePropertyOverride.ForeignValueWarning(DeviceGlobals.DebugMonoExtra, "something else entirely", Now));
    }

    [Fact]
    public void AProfilerProperty_IsAMark_WhateverItHolds()
    {
        var warning = DevicePropertyOverride.ForeignValueWarning(DeviceGlobals.DebugMonoProfile, "127.0.0.1:9000,suspend,connect", Now, deadlineRequired: false);
        Assert.NotNull(warning);
        Assert.Contains(DeviceGlobals.DebugMonoProfile, warning);
        Assert.Contains("127.0.0.1:9000,suspend,connect", warning);
        Assert.Contains("device-global", warning);
    }
}

/// <summary>Apply and restore on a real device, with a property of our own so nothing on the device is disturbed.</summary>
[Trait("Category", "Device")]
[Collection(DeviceCollection.Name)]
public sealed class DevicePropertyOverrideDeviceTests(DeviceFixture device)
{
    private const string Property = "debug.netandroid.device.tests";

    [Fact]
    public async Task Apply_RemembersWhatWasThere_AndRestorePutsItBack()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var ct = cts.Token;
        await device.Adb.SetPropAsync(device.Serial, Property, "before", ct);
        try
        {
            var over = new DevicePropertyOverride(device.Adb, device.Serial, Property);
            Assert.False(over.IsApplied);

            await over.ApplyAsync("first", ct);
            await over.ApplyAsync("second", ct);
            Assert.True(over.IsApplied);
            Assert.Equal("before", over.PreviousValue);
            Assert.Equal("second", await device.Adb.GetPropAsync(device.Serial, Property, ct));

            await over.RestoreAsync(ct);
            Assert.False(over.IsApplied);
            Assert.Equal("before", await device.Adb.GetPropAsync(device.Serial, Property, ct));
        }
        finally
        {
            await device.Adb.SetPropAsync(device.Serial, Property, "", CancellationToken.None);
        }
    }

    [Fact]
    public async Task Clear_EmptiesTheProperty_WhateverWasThere_AndDoesNothingWhenNeverApplied()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var ct = cts.Token;
        await device.Adb.SetPropAsync(device.Serial, Property, "before", ct);
        try
        {
            var over = new DevicePropertyOverride(device.Adb, device.Serial, Property);
            await over.ClearAsync(ct);   // never applied: nothing happens
            Assert.Equal("before", await device.Adb.GetPropAsync(device.Serial, Property, ct));

            await over.ApplyAsync("ours", ct);
            await over.ClearAsync(ct);
            Assert.False(over.IsApplied);
            Assert.Equal("", await device.Adb.GetPropAsync(device.Serial, Property, ct));
        }
        finally
        {
            await device.Adb.SetPropAsync(device.Serial, Property, "", CancellationToken.None);
        }
    }

    [Fact]
    public async Task Restore_OfAnEmptyPrevious_ClearsTheProperty_AndDoesNothingWhenNeverApplied()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var ct = cts.Token;
        await device.Adb.SetPropAsync(device.Serial, Property, "", ct);
        var over = new DevicePropertyOverride(device.Adb, device.Serial, Property);

        await over.RestoreAsync(ct);   // never applied: nothing happens
        Assert.Equal("", await device.Adb.GetPropAsync(device.Serial, Property, ct));

        await over.ApplyAsync("ours", ct);
        Assert.Equal("ours", await device.Adb.GetPropAsync(device.Serial, Property, ct));
        await over.RestoreAsync(ct);
        Assert.Equal("", await device.Adb.GetPropAsync(device.Serial, Property, ct));
    }
}
