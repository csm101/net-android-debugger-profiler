using Xunit.Abstractions;

namespace NetAndroid.Device.Tests;

/// <summary>
/// Driving the device's screen through adb alone, against the debugger's TestTarget on the selected
/// device: seeing it (screenshot, hierarchy) and acting on it (tap, type). Input injection is the one
/// part a vendor may gate; the first test says so in words when it is. What needs a debug session -
/// a tap that lands on a breakpoint, the hierarchy of a suspended app - stays in the debugger's own
/// DeviceControlTests.
/// </summary>
[Trait("Category", "Device")]
[Collection(DeviceCollection.Name)]
public sealed class DeviceControlTests(DeviceFixture device, ITestOutputHelper output)
{
    private DeviceControl Control => new(device.Adb, device.Serial, output.WriteLine);

    private async Task<UiTree> BringTestTargetToFrontAsync(CancellationToken ct)
    {
        Skip.IfNot(await ScreenApp.IsInstalledAsync(device.Adb, device.Serial, ct),
            $"{ScreenApp.Package} is not installed on {device.Serial}: the debugger's suite deploys it");
        await ScreenApp.LaunchAsync(Control, device.Adb, device.Serial, ct);
        return await ScreenApp.WaitOnScreenAsync(Control, output.WriteLine, ct);
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

    [SkippableFact]
    public async Task UiHierarchy_ListsTestTargetsControls_ByResourceId()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            var tree = await BringTestTargetToFrontAsync(cts.Token);

            var button = Assert.Single(UiHierarchy.Find(tree.Root, new UiSelector(ResourceId: "increment_button")));
            Assert.True(button.Clickable);
            Assert.Equal("Button", button.ShortClassName);
            Assert.Equal(ScreenApp.Package, button.Package);
            Assert.True(button.Bounds.Width > 0 && button.Bounds.Height > 0, button.Bounds.ToString());

            var label = Assert.Single(UiHierarchy.Find(tree.Root, new UiSelector(ResourceId: "counter_label")));
            Assert.StartsWith("Counter: ", label.Text);
            Assert.Single(UiHierarchy.Find(tree.Root, new UiSelector(ResourceId: "input_field")));
        }
        finally
        {
            await device.Adb.ForceStopAsync(device.Serial, ScreenApp.Package, CancellationToken.None);
        }
    }

    [SkippableFact]
    public async Task TypeText_IntoTheFocusedField_ShowsUpInTheHierarchy()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            var tree = await BringTestTargetToFrontAsync(cts.Token);
            var field = Assert.Single(UiHierarchy.Find(tree.Root, new UiSelector(ResourceId: "input_field")));

            var control = Control;
            await control.TapAsync(field.Bounds.CenterX, field.Bounds.CenterY, cts.Token);
            await control.TypeTextAsync("hello world's", cts.Token);
            await Task.Delay(500, cts.Token);

            // The field got focus, so an IME came up; on this emulator image that can be a crash dialog.
            var after = await control.DumpUiAsync(cts.Token);
            await ScreenApp.DismissCrashDialogAsync(control, after, output.WriteLine, cts.Token);
            after = await ScreenApp.WaitOnScreenAsync(control, output.WriteLine, cts.Token);
            var typed = Assert.Single(UiHierarchy.Find(after.Root, new UiSelector(ResourceId: "input_field")));
            Assert.Equal("hello world's", typed.Text);
            await control.PressKeyAsync("BACK", cts.Token); // hides the keyboard, leaves the activity
        }
        finally
        {
            await device.Adb.ForceStopAsync(device.Serial, ScreenApp.Package, CancellationToken.None);
        }
    }

    [Fact]
    public async Task TypeText_RefusesNonAscii_InsteadOfTypingGarbage()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var ex = await Assert.ThrowsAsync<DeviceControlException>(() => Control.TypeTextAsync("caffè", cts.Token));
        Assert.Contains("ASCII", ex.Message);
    }
}
