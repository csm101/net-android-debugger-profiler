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

    /// <summary>
    /// dotnet-dsrouter, from the copy shipped in the package if there is one, otherwise
    /// from the global tool or PATH.
    /// </summary>
    public static string? FindDsRouter() => FindDotnetTool("dotnet-dsrouter");

    /// <summary>
    /// A .NET tool the profiler drives as a process. Looked up, in order: the explicit
    /// override, the copy inside the package, the user's global tools, PATH.
    ///
    /// The package copy comes first on purpose: an installation that carries its own
    /// dsrouter must not start behaving differently because the machine happens to have
    /// another version installed globally.
    /// </summary>
    /// <param name="name">Tool name without extension, as in "dotnet-dsrouter".</param>
    /// <param name="appBase">Where the application lives; defaults to this process's base directory.</param>
    public static string? FindDotnetTool(string name, string? appBase = null)
    {
        string exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        string? fromEnv = Environment.GetEnvironmentVariable("NETANDROIDPROFILER_" + name.Replace('-', '_').ToUpperInvariant());
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        // The package keeps its tools next to bin/, so the parent is searched as well.
        string root = appBase ?? AppContext.BaseDirectory;
        foreach (var candidate in new[]
        {
            Path.Combine(root, "tools", exe),
            Path.Combine(root, exe),
            Path.Combine(root, "..", "tools", exe),
        })
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);

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
