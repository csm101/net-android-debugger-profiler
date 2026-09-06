using NetAndroidDebugger.Core;
using NetAndroidDebugger.Core.Adb;
using NetAndroidDebugger.Core.Device;
using NetAndroidDebugger.Tests.Harness;
using Xunit.Abstractions;

namespace NetAndroidDebugger.Tests;

/// <summary>
/// Driving the device's screen through adb, against TestTarget on the selected device: seeing it
/// (screenshot, hierarchy) and acting on it (tap, type) - and the loop that matters, a tap that
/// lands on a breakpoint. Input injection is the one part a vendor may gate; the first test says
/// so in words when it is.
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
    public async Task InputInjection_IsAllowed_OnThisDevice()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var (allowed, detail) = await Control.CheckInputInjectionAsync(cts.Token);
        // On a MIUI device this fails until "USB debugging (Security settings)" is on; the detail
        // says exactly that. Screenshots and the hierarchy do not need it, and neither does debugging.
        Assert.True(allowed, detail);
    }

    [Fact]
    public async Task Screenshot_IsAPng_TheSizeOfTheDisplay()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var control = Control;
        await control.WakeScreenAsync(cts.Token);
        var display = await control.GetDisplayInfoAsync(cts.Token);
        var shot = await control.CaptureScreenshotAsync(cts.Token);

        Assert.True(shot.Png.Length > 1000, $"{shot.Png.Length} bytes");
        // A rotated display swaps the two; either way the screenshot covers the whole display.
        var sameOrientation = shot.Width == display.Width && shot.Height == display.Height;
        var rotated = shot.Width == display.Height && shot.Height == display.Width;
        Assert.True(sameOrientation || rotated, $"screenshot {shot.Width}x{shot.Height} vs display {display.Width}x{display.Height}");
        Assert.True(display.Density > 0);
    }

    [Fact]
    public async Task UiHierarchy_ListsTestTargetsControls_ByResourceId()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var session = await LaunchAsync(cts.Token);
        var tree = await WaitForTestTargetOnScreenAsync(cts.Token);

        var button = Assert.Single(UiHierarchy.Find(tree.Root, new UiSelector(ResourceId: "increment_button")));
        Assert.True(button.Clickable);
        Assert.Equal("Button", button.ShortClassName);
        Assert.Equal(TestEnvironment.TestTargetPackage, button.Package);
        Assert.True(button.Bounds.Width > 0 && button.Bounds.Height > 0, button.Bounds.ToString());

        var label = Assert.Single(UiHierarchy.Find(tree.Root, new UiSelector(ResourceId: "counter_label")));
        Assert.StartsWith("Counter: ", label.Text);
        Assert.Single(UiHierarchy.Find(tree.Root, new UiSelector(ResourceId: "input_field")));
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

    [Fact]
    public async Task TypeText_IntoTheFocusedField_ShowsUpInTheHierarchy()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var session = await LaunchAsync(cts.Token);
        var tree = await WaitForTestTargetOnScreenAsync(cts.Token);
        var field = Assert.Single(UiHierarchy.Find(tree.Root, new UiSelector(ResourceId: "input_field")));

        var control = Control;
        await control.TapAsync(field.Bounds.CenterX, field.Bounds.CenterY, cts.Token);
        await control.TypeTextAsync("hello world's", cts.Token);
        await Task.Delay(500, cts.Token);

        // The field got focus, so an IME came up; on this emulator image that can be a crash dialog.
        var after = await control.DumpUiAsync(cts.Token);
        await DismissCrashDialogAsync(after, cts.Token);
        after = await WaitForTestTargetOnScreenAsync(cts.Token);
        var typed = Assert.Single(UiHierarchy.Find(after.Root, new UiSelector(ResourceId: "input_field")));
        Assert.Equal("hello world's", typed.Text);
        await control.PressKeyAsync("BACK", cts.Token); // hides the keyboard, leaves the activity
    }

    [Fact]
    public async Task TypeText_RefusesNonAscii_InsteadOfTypingGarbage()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var ex = await Assert.ThrowsAsync<DeviceControlException>(() => Control.TypeTextAsync("caffè", cts.Token));
        Assert.Contains("ASCII", ex.Message);
    }
}
