namespace NetAndroidProfiler.Core.Devices;

/// <summary>Finds the external tools the collector depends on (adb, dotnet-dsrouter).</summary>
public static class ToolLocator
{
    /// <summary>
    /// adb, through the locator both products share (<see cref="AdbLocator"/>: an explicit
    /// <c>NETANDROIDPROFILER_ADB</c>, then <c>NAD_ADB_PATH</c>, the SDK variables, the registry, the SDK's
    /// default folders, PATH last). Null when nothing is found; a variable that names a wrong path is
    /// an error the locator reports, never a value to skip.
    /// </summary>
    public static string? FindAdb()
    {
        try { return AdbLocator.Locate(Environment.GetEnvironmentVariable("NETANDROIDPROFILER_ADB"), "NETANDROIDPROFILER_ADB").Path; }
        catch (AdbNotFoundException) { return null; }
    }

    /// <summary>
    /// dotnet-dsrouter, from the copy shipped in the package if there is one, otherwise
    /// from the global tool or PATH.
    /// </summary>
    public static string? FindDsRouter() => FindDotnetTool("dotnet-dsrouter");

    /// <summary>
    /// The weaver the build-time weaving targets run, and the collector assembly the woven app
    /// references. Both ship in the package's build/tools/ folder next to the targets file;
    /// in a source tree that folder is written by the packaging step, so an ordinary build's
    /// output is where they are. Answered here rather than in the targets file because this is
    /// where every other "where is my binary" question is answered, and because a path with a
    /// target framework in it does not belong in an msbuild condition that nobody rebuilds.
    /// </summary>
    public static string? FindWeaveTool(string? appBase = null) =>
        FindShippedTool("nap-weave.dll", Path.Combine("src", "NetAndroidProfiler.Weave", "bin"), appBase);

    /// <inheritdoc cref="FindWeaveTool"/>
    public static string? FindCollectorAssembly(string? appBase = null) =>
        FindShippedTool("NetAndroidProfiler.Collector.dll", Path.Combine("src", "NetAndroidProfiler.Collector", "bin"), appBase);

    /// <summary>
    /// A file that ships in the package's build/tools/, looked for there first and then in the
    /// build output of the project that produces it. The configuration and the target framework
    /// are not spelled out: whatever is under bin/ is searched, newest first, so a retargeted
    /// project keeps working without anyone remembering to edit a path.
    /// </summary>
    private static string? FindShippedTool(string fileName, string projectBin, string? appBase)
    {
        var dir = new DirectoryInfo(appBase ?? AppContext.BaseDirectory);
        for (int i = 0; dir is not null && i < 8; i++, dir = dir.Parent)
        {
            var shipped = Path.Combine(dir.FullName, "build", "tools", fileName);
            if (File.Exists(shipped)) return Path.GetFullPath(shipped);

            var bin = Path.Combine(dir.FullName, projectBin);
            if (!Directory.Exists(bin)) continue;
            var built = new DirectoryInfo(bin).GetFiles(fileName, SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (built is not null) return built.FullName;
        }
        return null;
    }

    /// <summary>
    /// The desktop GUI. It ships in the package's gui/ folder next to bin/, and lives in
    /// gui/ at the root of a source tree, so both are searched - the same shape as
    /// <see cref="FindWeavingTargets"/>. The GUI is optional: a machine without it profiles
    /// exactly as well, it just has nothing to draw a picture with.
    /// </summary>
    public static string? FindGui(string? appBase = null)
    {
        const string exe = "NapGui.exe";
        var fromEnv = Environment.GetEnvironmentVariable("NETANDROIDPROFILER_NAPGUI");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return Path.GetFullPath(fromEnv);
        if (!OperatingSystem.IsWindows()) return null;

        var dir = new DirectoryInfo(appBase ?? AppContext.BaseDirectory);
        for (int i = 0; dir is not null && i < 8; i++, dir = dir.Parent)
        {
            foreach (var candidate in new[] { Path.Combine(dir.FullName, exe), Path.Combine(dir.FullName, "gui", exe) })
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    /// <summary>
    /// The msbuild targets that weave an app while it is built, for apps that keep their
    /// assemblies inside the APK and cannot be rewritten on the device. Shipped in the
    /// package's build/ folder, and found in the repository the same way.
    /// </summary>
    public static string? FindWeavingTargets(string? appBase = null)
    {
        const string name = "NetAndroidProfiler.Weaving.targets";
        var dir = new DirectoryInfo(appBase ?? AppContext.BaseDirectory);
        // bin/ next to build/ in the package, and a few levels up in a source tree.
        for (int i = 0; dir is not null && i < 8; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "build", name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>The .NET SDK driver, needed to build and install an app project.</summary>
    public static string? FindDotnet()
    {
        string exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        string? root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, exe))) return Path.Combine(root, exe);
        return FindOnPath(exe);
    }

    /// <summary>
    /// The external tools the profiler drives, each with what it is for and how to get it.
    /// A frontend shows this instead of failing halfway through a session with a message
    /// about a missing executable - dsrouter in particular is a one-line install that
    /// nobody remembers until the first session dies.
    /// </summary>
    public static IReadOnlyList<ToolStatus> Prerequisites() =>
    [
        new ToolStatus(
            "adb", FindAdb(), Required: true,
            "Talks to the device: install, launch, port forwarding, logcat.",
            InstallCommand: null,
            "Install the Android SDK platform-tools; adb is looked for through NAD_ADB_PATH, ANDROID_HOME or ANDROID_SDK_ROOT, the SDK the .NET Android workload registered, the SDK's default folders, then PATH."),
        new ToolStatus(
            "dotnet-dsrouter", FindDsRouter(), Required: true,
            "Routes the app's diagnostics port over adb; every profiling session goes through it.",
            InstallCommand: "dotnet tool install -g dotnet-dsrouter",
            "Install it as a global .NET tool: dotnet tool install -g dotnet-dsrouter"),
        new ToolStatus(
            "NapGui", FindGui(), Required: false,
            "Shows a session, and draws its panels for whoever asks: the pictures in a report come from it.",
            InstallCommand: null,
            "It ships in the package's gui folder, beside bin. Point NETANDROIDPROFILER_NAPGUI at it if it lives elsewhere."),
        new ToolStatus(
            "dotnet", FindDotnet(), Required: false,
            "Builds and installs an app project from the GUI; not needed to profile an app that is already installed.",
            InstallCommand: null,
            "Install the .NET SDK, or put dotnet on PATH."),
    ];

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

    /// <summary>
    /// Installs a missing prerequisite that has an install command, streaming the output.
    /// Only tools this class knows about can be installed: the command is not something a
    /// caller supplies.
    /// </summary>
    public static async Task<int> InstallAsync(string toolName, Action<string> log, CancellationToken ct)
    {
        var tool = Prerequisites().FirstOrDefault(t => t.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ToolException($"'{toolName}' is not one of the tools this profiler installs.");
        if (tool.InstallCommand is null)
            throw new ToolException($"{tool.Name} cannot be installed automatically. {tool.Fix}");

        string dotnet = FindDotnet()
            ?? throw new ToolException("dotnet was not found on this machine: install the .NET SDK, or put dotnet on PATH.");
        string[] install = ["tool", "install", "-g", tool.Name];
        log($"{dotnet} {string.Join(' ', install)}");
        int code = await ProcessRunner.RunStreamingAsync(dotnet, install, log, ct).ConfigureAwait(false);
        if (code != 0)
        {
            // Already installed is the common "failure" here, and the useful answer to it
            // is to bring it up to date rather than to report an error.
            log("Install did not succeed; trying an update in case it is already installed.");
            string[] update = ["tool", "update", "-g", tool.Name];
            log($"{dotnet} {string.Join(' ', update)}");
            code = await ProcessRunner.RunStreamingAsync(dotnet, update, log, ct).ConfigureAwait(false);
        }

        string? found = FindDotnetTool(tool.Name);
        log(found is not null ? $"{tool.Name} is now at {found}" : $"{tool.Name} is still not where the profiler looks for it.");
        return found is not null ? 0 : code == 0 ? 1 : code;
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

/// <summary>An external tool the profiler needs, where it was found, and what to do when it was not.</summary>
/// <param name="Name">Tool name, as it is installed (adb, dotnet-dsrouter, dotnet).</param>
/// <param name="Path">Full path, or null when the tool is missing.</param>
/// <param name="Required">Whether profiling is impossible without it.</param>
/// <param name="Purpose">What the profiler uses it for.</param>
/// <param name="InstallCommand">The command that installs it, when there is one a frontend can run.</param>
/// <param name="Fix">What the user should do when it is missing.</param>
public sealed record ToolStatus(
    string Name, string? Path, bool Required, string Purpose, string? InstallCommand, string Fix)
{
    /// <summary>Whether the tool was found.</summary>
    public bool Found => Path is not null;
}
