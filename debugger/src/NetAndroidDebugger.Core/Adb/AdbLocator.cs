using Microsoft.Win32;

namespace NetAndroidDebugger.Core.Adb;

/// <summary>Where adb was found and what said so, for the log and for error messages.</summary>
public sealed record AdbLocation(string Path, string Source)
{
    public override string ToString() => $"{Path} (from {Source})";
}

/// <summary>
/// Finds the adb executable without assuming it is on PATH, which on a machine set up by Visual
/// Studio it usually is not. Most explicit source first, and a source that was named but is wrong
/// is an error rather than a fallback, as with device serials: a stale value would otherwise run
/// some other adb while looking like it worked.
/// <list type="number">
/// <item>An explicit path (a call argument or <c>adbPath</c> in launch.json).</item>
/// <item><c>NAD_ADB_PATH</c>: the per-machine answer, like <c>NAD_DEVICE_SERIAL</c>.</item>
/// <item><c>ANDROID_HOME</c> / <c>ANDROID_SDK_ROOT</c>, the SDK conventions msbuild honours.</item>
/// <item>The SDK directory the .NET Android workload and Android Studio record in the registry.</item>
/// <item>The SDK's default install folders.</item>
/// <item>PATH, last.</item>
/// </list>
/// </summary>
public static class AdbLocator
{
    public const string PathVariable = "NAD_ADB_PATH";

    private static readonly string[] SdkVariables = ["ANDROID_HOME", "ANDROID_SDK_ROOT"];

    /// <summary>What the locator asks the machine; injectable so the precedence can be tested without one.</summary>
    public sealed record Environment(
        Func<string, string?> GetVariable,
        Func<string, bool> FileExists,
        Func<IEnumerable<(string Directory, string Source)>> RegisteredSdks,
        Func<IEnumerable<(string Directory, string Source)>> DefaultSdks,
        bool IsWindows)
    {
        public static Environment Real { get; } = new(
            System.Environment.GetEnvironmentVariable,
            File.Exists,
            ReadRegisteredSdks,
            DefaultSdkFolders,
            OperatingSystem.IsWindows());
    }

    /// <summary>Resolves adb on this machine. Throws <see cref="LaunchException"/> naming every source tried when nothing is found.</summary>
    public static AdbLocation Locate(string? explicitPath = null, string explicitSource = "the call")
        => Locate(explicitPath, explicitSource, Environment.Real);

    public static AdbLocation Locate(string? explicitPath, string explicitSource, Environment env)
    {
        var exe = env.IsWindows ? "adb.exe" : "adb";
        var tried = new List<string>();

        if (!string.IsNullOrWhiteSpace(explicitPath))
            return Named(explicitPath, explicitSource);

        var fromVariable = env.GetVariable(PathVariable);
        if (!string.IsNullOrWhiteSpace(fromVariable))
            return Named(fromVariable, PathVariable);
        tried.Add($"{PathVariable} (not set)");

        foreach (var variable in SdkVariables)
        {
            var sdk = env.GetVariable(variable);
            if (string.IsNullOrWhiteSpace(sdk)) { tried.Add($"{variable} (not set)"); continue; }
            if (InSdk(sdk, variable) is { } found) return found;
        }

        foreach (var (dir, source) in env.RegisteredSdks())
            if (InSdk(dir, source) is { } found) return found;

        foreach (var (dir, source) in env.DefaultSdks())
            if (InSdk(dir, source) is { } found) return found;

        foreach (var dir in (env.GetVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = System.IO.Path.Combine(dir, exe);
            if (env.FileExists(candidate)) return new AdbLocation(candidate, "PATH");
        }
        tried.Add("PATH (no adb in it)");

        throw new LaunchException(
            $"adb not found. Tried, in order: {string.Join("; ", tried)}. Install the Android SDK platform-tools, " +
            $"or point {PathVariable} at adb, or set ANDROID_HOME, or give adbPath in the launch configuration.");

        AdbLocation Named(string path, string source)
        {
            // A directory is accepted too: people name the SDK or platform-tools rather than the exe.
            var full = System.IO.Path.GetFullPath(path.Trim());
            if (env.FileExists(full)) return new AdbLocation(full, source);
            foreach (var inside in new[] { System.IO.Path.Combine(full, exe), System.IO.Path.Combine(full, "platform-tools", exe) })
                if (env.FileExists(inside)) return new AdbLocation(inside, source);
            throw new LaunchException($"{source} names adb as '{path}', but nothing is there. A wrong value is never replaced by a guess: fix or remove it.");
        }

        AdbLocation? InSdk(string sdkDirectory, string source)
        {
            var candidate = System.IO.Path.Combine(sdkDirectory.Trim(), "platform-tools", exe);
            if (env.FileExists(candidate)) return new AdbLocation(candidate, source);
            tried.Add($"{source} ({candidate} does not exist)");
            return null;
        }
    }

    /// <summary>
    /// SDK directories recorded by installers. The .NET Android workload (via Visual Studio) writes
    /// <c>HKCU\SOFTWARE\Novell\Mono for Android\AndroidSdkDirectory</c> - a key that predates the
    /// rename and is still what msbuild reads; Android Studio writes <c>HKLM\SOFTWARE\Android Studio\SdkPath</c>
    /// (often empty); the old standalone SDK installer wrote <c>Android SDK Tools\Path</c>.
    /// </summary>
    private static IEnumerable<(string Directory, string Source)> ReadRegisteredSdks()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        foreach (var (hive, key, value, source) in new[]
        {
            (Registry.CurrentUser, @"SOFTWARE\Novell\Mono for Android", "AndroidSdkDirectory", "the .NET Android workload's registry key"),
            (Registry.LocalMachine, @"SOFTWARE\Android Studio", "SdkPath", "Android Studio's registry key"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Android SDK Tools", "Path", "the Android SDK installer's registry key"),
            (Registry.LocalMachine, @"SOFTWARE\Android SDK Tools", "Path", "the Android SDK installer's registry key"),
        })
        {
            string? dir = null;
            try
            {
                using var k = hive.OpenSubKey(key);
                dir = k?.GetValue(value) as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            if (!string.IsNullOrWhiteSpace(dir)) yield return (dir, source);
        }
    }

    private static IEnumerable<(string Directory, string Source)> DefaultSdkFolders()
    {
        if (OperatingSystem.IsWindows())
        {
            var local = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
            if (local.Length > 0) yield return (System.IO.Path.Combine(local, "Android", "Sdk"), "the SDK's default folder");
            var programs = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86);
            if (programs.Length > 0) yield return (System.IO.Path.Combine(programs, "Android", "android-sdk"), "Visual Studio's SDK folder");
            yield break;
        }
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        if (home.Length == 0) yield break;
        yield return (System.IO.Path.Combine(home, "Library", "Android", "sdk"), "the SDK's default folder");
        yield return (System.IO.Path.Combine(home, "Android", "Sdk"), "the SDK's default folder");
    }
}
