using NetAndroidDebugger.Core;
using NetAndroidDebugger.Core.Adb;

namespace NetAndroidDebugger.Tests;

/// <summary>
/// Finding adb without assuming PATH. No device and no real machine: every source the locator
/// consults is injected, so what is tested is the order and the rule that a named source which is
/// wrong stops the search rather than being replaced by a guess.
/// </summary>
public sealed class AdbLocatorTests
{
    private const string Exe = @"C:\sdk\platform-tools\adb.exe";

    private static AdbLocator.Environment Machine(
        IReadOnlyDictionary<string, string>? env = null,
        IReadOnlyCollection<string>? files = null,
        IEnumerable<(string, string)>? registry = null,
        IEnumerable<(string, string)>? defaults = null)
        => new(
            name => env is not null && env.TryGetValue(name, out var v) ? v : null,
            path => files is not null && files.Contains(path, StringComparer.OrdinalIgnoreCase),
            () => registry ?? [],
            () => defaults ?? [],
            IsWindows: true);

    [Fact]
    public void ExplicitPath_Wins_AndAcceptsTheExe_ItsFolder_OrTheSdk()
    {
        var machine = Machine(files: [Exe]);
        Assert.Equal(new AdbLocation(Exe, "the call"), AdbLocator.Locate(Exe, "the call", machine));
        Assert.Equal(Exe, AdbLocator.Locate(@"C:\sdk\platform-tools", "the call", machine).Path);
        Assert.Equal(Exe, AdbLocator.Locate(@"C:\sdk", "launch.json", machine).Path);
        Assert.Equal("launch.json", AdbLocator.Locate(@"C:\sdk", "launch.json", machine).Source);
    }

    [Fact]
    public void ExplicitPath_ThatIsWrong_IsAnError_NotAFallback()
    {
        // PATH would find it, and must not: a named source that is wrong is the user's to fix.
        var machine = Machine(env: new Dictionary<string, string> { ["PATH"] = @"C:\sdk\platform-tools" }, files: [Exe]);
        var ex = Assert.Throws<LaunchException>(() => AdbLocator.Locate(@"D:\nowhere\adb.exe", "the call", machine));
        Assert.Contains("the call", ex.Message);
        Assert.Contains(@"D:\nowhere\adb.exe", ex.Message);
    }

    [Fact]
    public void NadAdbPath_Wins_OverTheSdkVariables_AndIsAnErrorWhenWrong()
    {
        var other = @"C:\other\platform-tools\adb.exe";
        var good = Machine(
            env: new Dictionary<string, string> { [AdbLocator.PathVariable] = other, ["ANDROID_HOME"] = @"C:\sdk" },
            files: [Exe, other]);
        Assert.Equal(new AdbLocation(other, AdbLocator.PathVariable), AdbLocator.Locate(null, "the call", good));

        var stale = Machine(
            env: new Dictionary<string, string> { [AdbLocator.PathVariable] = @"C:\gone\adb.exe", ["ANDROID_HOME"] = @"C:\sdk" },
            files: [Exe]);
        var ex = Assert.Throws<LaunchException>(() => AdbLocator.Locate(null, "the call", stale));
        Assert.Contains(AdbLocator.PathVariable, ex.Message);
    }

    [Fact]
    public void SdkVariables_Registry_Defaults_ThenPath_InThatOrder()
    {
        var fromHome = @"C:\home\platform-tools\adb.exe";
        var fromRoot = @"C:\root\platform-tools\adb.exe";
        var fromRegistry = @"C:\reg\platform-tools\adb.exe";
        var fromDefault = @"C:\def\platform-tools\adb.exe";
        var fromPath = @"C:\onpath\adb.exe";
        var env = new Dictionary<string, string>
        {
            ["ANDROID_HOME"] = @"C:\home",
            ["ANDROID_SDK_ROOT"] = @"C:\root",
            ["PATH"] = @"C:\tools;C:\onpath",
        };
        var registry = new[] { (@"C:\reg", "the .NET Android workload's registry key") };
        var defaults = new[] { (@"C:\def", "the SDK's default folder") };

        AdbLocation Locate(params string[] present)
            => AdbLocator.Locate(null, "the call", Machine(env, present, registry, defaults));

        Assert.Equal(new AdbLocation(fromHome, "ANDROID_HOME"), Locate(fromHome, fromRoot, fromRegistry, fromDefault, fromPath));
        Assert.Equal(new AdbLocation(fromRoot, "ANDROID_SDK_ROOT"), Locate(fromRoot, fromRegistry, fromDefault, fromPath));
        Assert.Equal(new AdbLocation(fromRegistry, "the .NET Android workload's registry key"), Locate(fromRegistry, fromDefault, fromPath));
        Assert.Equal(new AdbLocation(fromDefault, "the SDK's default folder"), Locate(fromDefault, fromPath));
        Assert.Equal(new AdbLocation(fromPath, "PATH"), Locate(fromPath));
    }

    [Fact]
    public void NothingFound_ListsEverySourceTried()
    {
        var machine = Machine(env: new Dictionary<string, string> { ["ANDROID_HOME"] = @"C:\empty", ["PATH"] = @"C:\tools" });
        var ex = Assert.Throws<LaunchException>(() => AdbLocator.Locate(null, "the call", machine));
        Assert.Contains(AdbLocator.PathVariable, ex.Message);
        Assert.Contains(@"C:\empty\platform-tools\adb.exe", ex.Message);
        Assert.Contains("ANDROID_SDK_ROOT (not set)", ex.Message);
        Assert.Contains("PATH", ex.Message);
    }

    [Fact]
    public void OnThisMachine_AdbIsFound_WithoutPath()
    {
        // The real resolver, with PATH emptied: Visual Studio's registry entry or the default SDK
        // folder has to be enough, which is the case this exists for.
        var real = AdbLocator.Environment.Real;
        var noPath = real with { GetVariable = name => name == "PATH" ? "" : real.GetVariable(name) };
        var found = AdbLocator.Locate(null, "the call", noPath);
        Assert.True(File.Exists(found.Path), found.ToString());
        Assert.NotEqual("PATH", found.Source);
    }
}
