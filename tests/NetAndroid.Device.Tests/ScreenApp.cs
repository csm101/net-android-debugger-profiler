using System.Text.RegularExpressions;

namespace NetAndroid.Device.Tests;

/// <summary>
/// The app the screen tests look at: the debugger's TestTarget, which has a button, a label and a
/// text field with stable resource ids. Its package comes from its project file, never from a
/// literal; the debugger's suite deploys it, and the tests here skip when it is not installed.
/// </summary>
public static class ScreenApp
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string ProjectPath => Path.Combine(RepoRoot, "TestTarget", "Debugger", "TestTarget.csproj");

    public static string Package { get; } = ReadApplicationId(ProjectPath);

    public static async Task<bool> IsInstalledAsync(AdbClient adb, string serial, CancellationToken ct)
        => (await adb.PackagePathsAsync(serial, package: Package, ct)).Count > 0;

    /// <summary>Wakes the screen, force-stops the app and starts it again through adb alone.</summary>
    public static async Task LaunchAsync(DeviceControl control, AdbClient adb, string serial, CancellationToken ct)
    {
        await control.WakeScreenAsync(ct);
        await adb.ForceStopAsync(serial, Package, ct);
        await adb.LaunchAsync(serial, Package, ct);
    }

    /// <summary>The activity is on screen once its button is in the hierarchy; a launch returns before that.</summary>
    public static async Task<UiTree> WaitOnScreenAsync(DeviceControl control, Action<string> log, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            UiTree? tree = null;
            try { tree = await control.DumpUiAsync(ct); }
            catch (DeviceControlException ex) when (DateTime.UtcNow < deadline) { log("ui dump not ready: " + ex.Message); }
            if (tree is not null && UiHierarchy.Find(tree.Root, new UiSelector(ResourceId: "increment_button")).Count == 1)
                return tree;
            if (tree is not null) await DismissCrashDialogAsync(control, tree, log, ct);
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"{Package}'s activity did not appear on screen");
            await Task.Delay(500, ct);
        }
    }

    /// <summary>
    /// Android's "X keeps stopping" dialog covers the activity and owns the hierarchy. It is the
    /// environment, not the code under test: on the API 30 emulator image Gboard crash-loops in an
    /// ML Kit initializer the moment a text field gets focus. Tapping "Close app" clears it.
    /// </summary>
    public static async Task DismissCrashDialogAsync(DeviceControl control, UiTree tree, Action<string> log, CancellationToken ct)
    {
        var close = UiHierarchy.Find(tree.Root, new UiSelector(ResourceId: "android:id/aerr_close")).FirstOrDefault();
        if (close is null) return;
        var title = UiHierarchy.Find(tree.Root, new UiSelector(ResourceId: "android:id/alertTitle")).FirstOrDefault()?.Text;
        log($"closing a crash dialog that covers the activity: '{title}'");
        await control.TapAsync(close.Bounds.CenterX, close.Bounds.CenterY, ct);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NetAndroidDebuggerProfiler.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("repository root (NetAndroidDebuggerProfiler.slnx) not found above " + AppContext.BaseDirectory);
    }

    private static string ReadApplicationId(string csproj)
    {
        var m = Regex.Match(File.ReadAllText(csproj), @"<ApplicationId>(?<id>[^<]+)</ApplicationId>");
        if (!m.Success) throw new InvalidOperationException("ApplicationId not found in " + csproj);
        return m.Groups["id"].Value.Trim();
    }
}
