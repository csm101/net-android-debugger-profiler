using NetAndroidDebugger.Core;
using NetAndroidDebugger.Tests.Harness;
using Xunit.Abstractions;

namespace NetAndroidDebugger.Tests;

/// <summary>
/// The screen and the debugger together, against TestTarget on the selected device: a tap that lands
/// on a breakpoint, and a hierarchy that cannot be read while the app is suspended. Driving the
/// screen on its own (screenshot, hierarchy, tap, type, the vendor's input gate) is covered by
/// NetAndroid.Device.Tests, where DeviceControl now lives.
/// </summary>
[Collection(DeviceCollection.Name)]
public sealed class DeviceControlTests(DeviceFixture device, ITestOutputHelper output)
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(20);
    private const string ClickMarker = "_counter = previous + 1;";

    private DeviceControl Control => new(new AdbClient(), device.Serial, output.WriteLine);

    private async Task<DebugSession> LaunchAsync(CancellationToken ct, Action<DebugSession>? beforeLaunch = null)
    {
        await Control.WakeScreenAsync(ct);
        var session = device.NewSession(output.WriteLine);
        beforeLaunch?.Invoke(session);
        await session.LaunchAsync(TestEnvironment.TestTargetApp(), device.Options(), ct);
        return session;
    }

    /// <summary>The activity is on screen once its button is in the hierarchy; `am start` returns before that.</summary>
    private async Task<UiTree> WaitForTestTargetOnScreenAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            UiTree? tree = null;
            try { tree = await Control.DumpUiAsync(ct); }
            catch (DeviceControlException ex) when (DateTime.UtcNow < deadline) { output.WriteLine("ui dump not ready: " + ex.Message); }
            if (tree is not null && UiHierarchy.Find(tree.Root, new UiSelector(ResourceId: "increment_button")).Count == 1)
                return tree;
            if (tree is not null) await DismissCrashDialogAsync(tree, ct);
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("TestTarget's activity did not appear on screen");
            await Task.Delay(500, ct);
        }
    }

    /// <summary>
    /// Android's "X keeps stopping" dialog covers the activity and owns the hierarchy. It is the
    /// environment, not the code under test: on the API 30 emulator image Gboard crash-loops in an
    /// ML Kit initializer the moment a text field gets focus. Tapping "Close app" clears it.
    /// </summary>
    private async Task DismissCrashDialogAsync(UiTree tree, CancellationToken ct)
    {
        var close = UiHierarchy.Find(tree.Root, new UiSelector(ResourceId: "android:id/aerr_close")).FirstOrDefault();
        if (close is null) return;
        var title = UiHierarchy.Find(tree.Root, new UiSelector(ResourceId: "android:id/alertTitle")).FirstOrDefault()?.Text;
        output.WriteLine($"closing a crash dialog that covers the activity: '{title}'");
        await Control.TapAsync(close.Bounds.CenterX, close.Bounds.CenterY, ct);
    }

    [Fact]
    public async Task Tap_OnTheButton_HitsTheClickHandlersBreakpoint()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var clickLine = TestEnvironment.LineOf(TestEnvironment.MainActivitySource, ClickMarker);
        await using var session = await LaunchAsync(cts.Token, s => s.SetBreakpoint(new BreakpointSpec(TestEnvironment.MainActivitySource, clickLine)));
        var tree = await WaitForTestTargetOnScreenAsync(cts.Token);
        var button = Assert.Single(UiHierarchy.Find(tree.Root, new UiSelector(ResourceId: "increment_button")));

        await Control.TapAsync(button.Bounds.CenterX, button.Bounds.CenterY, cts.Token);

        var stop = await session.WaitForStopAsync(0, StopTimeout, cts.Token);
        Assert.NotNull(stop);
        Assert.Equal(StopReason.Breakpoint, stop.Reason);
        Assert.Equal(clickLine, stop.Location?.Line);
        Assert.Equal("0", session.GetVariable(stop.Pid, stop.ThreadId, 0, "previous")?.Value);
    }

    [Fact]
    public async Task UiHierarchy_FailsInWords_WhileTheAppIsSuspended()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var clickLine = TestEnvironment.LineOf(TestEnvironment.MainActivitySource, ClickMarker);
        await using var session = await LaunchAsync(cts.Token, s => s.SetBreakpoint(new BreakpointSpec(TestEnvironment.MainActivitySource, clickLine)));
        var tree = await WaitForTestTargetOnScreenAsync(cts.Token);
        var button = Assert.Single(UiHierarchy.Find(tree.Root, new UiSelector(ResourceId: "increment_button")));
        await Control.TapAsync(button.Bounds.CenterX, button.Bounds.CenterY, cts.Token);
        var stop = await session.WaitForStopAsync(0, StopTimeout, cts.Token);
        Assert.NotNull(stop);

        // The main thread is suspended inside the click handler: uiautomator cannot get an idle UI,
        // and the failure must say why rather than look like a broken device. A screenshot still works.
        var ex = await Assert.ThrowsAsync<DeviceControlException>(() => Control.DumpUiAsync(cts.Token));
        Assert.Contains("suspended", ex.Message);
        var shot = await Control.CaptureScreenshotAsync(cts.Token);
        Assert.True(shot.Width > 0);
    }
}
