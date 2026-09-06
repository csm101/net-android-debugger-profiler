using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using NetAndroidDebugger.Mcp;
using NetAndroidProfiler.Mcp;
using DebugState = NetAndroidDebugger.Core.SessionState;

namespace NetAndroid.Mcp;

/// <summary>
/// The three tools both products define, in one version each: the two listings come from the
/// profiler's tools (the debugger's output is a subset of theirs), and the app-output tool
/// reads the debug session while one is active and the device's logcat otherwise.
/// </summary>
[McpServerToolType]
public sealed class SharedTools(DebuggerTools debugger, ProfilerTools profiler, NetAndroidDebugger.Mcp.SessionHost debugSessions)
{
    /// <summary>The tool names this class owns; <see cref="ToolCatalog"/> skips them in the product tool classes.</summary>
    public static readonly IReadOnlySet<string> Names =
        new HashSet<string>(StringComparer.Ordinal) { "list_devices", "list_app_projects", "get_app_output" };

    [McpServerTool(Name = "list_devices", ReadOnly = true), Description(
        "Lists adb devices/emulators (serial, state, model, API level, ABI). Pick a serial for launch_app, attach_to_app or profile_run.")]
    public async Task<string> ListDevices(CancellationToken ct)
    {
        // "adb not found" is the first thing a fresh machine says; the message lists where it looked.
        try { return await profiler.ListDevices(ct); }
        catch (AdbException ex) { throw new McpException(ex.Message); }
    }

    [McpServerTool(Name = "list_app_projects", ReadOnly = true), Description(
        "Reads a solution, a .csproj or a folder of sources and lists the .NET for Android application projects in it, " +
        "with the package they install, the build output holding their pdbs (the profiler's symbolsDir) and the assemblies " +
        "to weave. Libraries that target Android are not listed. The counterpart of list_devices for choosing what to " +
        "launch (launch_app) or profile (profile_run).")]
    public string ListAppProjects(
        [Description("Solution (.sln/.slnx), project (.csproj) or folder to search")] string? path = null,
        [Description("Build configuration whose output directory is reported")] string configuration = "Debug",
        [Description("Same as path (the debugger's name for it); give one of the two")] string? solutionOrFolder = null)
    {
        var where = path ?? solutionOrFolder;
        if (string.IsNullOrEmpty(where))
            throw new McpException("Pass path: a solution, a .csproj or a folder to search.");
        return profiler.ListAppProjects(where, configuration);
    }

    [McpServerTool(Name = "get_app_output", ReadOnly = true), Description(
        "Recent output of the app. While a debug session is active: the logcat lines of its processes plus what they wrote " +
        "to stdout/stderr (tags 'stdout'/'stderr'), oldest first, filters applied before the line limit. Otherwise: the " +
        "current logcat of the package's process on the device named by deviceSerial and packageName, most recent last.")]
    public async Task<string> GetAppOutput(
        [Description("adb serial (needed when no debug session is active)")] string? deviceSerial = null,
        [Description("Package name (needed when no debug session is active)")] string? packageName = null,
        [Description("Max lines (default 200)")] int maxLines = 200,
        [Description("Debug session only: lowest Android priority to include: V, D, I, W, E or F")] string? minLevel = null,
        [Description("Debug session only: lines whose tag contains this text")] string? tagContains = null,
        [Description("Debug session only: lines whose message contains this text")] string? contains = null,
        [Description("Debug session only: lines from this process id")] int? pid = null,
        CancellationToken ct = default)
    {
        if (debugSessions.Current is { State: not (DebugState.NotStarted or DebugState.Exited) })
            return debugger.GetAppOutput(maxLines, minLevel, tagContains, contains, pid);
        if (string.IsNullOrEmpty(deviceSerial) || string.IsNullOrEmpty(packageName))
            throw new McpException("No debug session is active, so the app has to be named: pass deviceSerial and packageName " +
                                   "(list_devices and list_app_projects say which).");
        return await profiler.GetAppOutput(deviceSerial, packageName, maxLines, ct);
    }
}
