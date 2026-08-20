namespace NetAndroidProfiler.Core.Devices;

/// <summary>Finds the external tools the collector depends on (adb, dotnet-dsrouter).</summary>
public static class ToolLocator
{
    public static string? FindAdb()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("NETANDROIDPROFILER_ADB");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;
        string? onPath = FindOnPath(OperatingSystem.IsWindows() ? "adb.exe" : "adb");
        if (onPath is not null) return onPath;
        foreach (var root in new[]
        {
            Environment.GetEnvironmentVariable("ANDROID_HOME"),
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Android", "android-sdk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk"),
        })
        {
            if (string.IsNullOrEmpty(root)) continue;
            string p = Path.Combine(root, "platform-tools", OperatingSystem.IsWindows() ? "adb.exe" : "adb");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>dotnet-dsrouter global tool (~/.dotnet/tools) or on PATH.</summary>
    public static string? FindDsRouter() => FindDotnetTool("dotnet-dsrouter");

    public static string? FindDotnetTool(string name)
    {
        string exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        string? fromEnv = Environment.GetEnvironmentVariable("NETANDROIDPROFILER_" + name.Replace('-', '_').ToUpperInvariant());
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;
        string tools = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", exe);
        if (File.Exists(tools)) return tools;
        return FindOnPath(exe);
    }

    private static string? FindOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string p = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(p)) return p;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }
}
