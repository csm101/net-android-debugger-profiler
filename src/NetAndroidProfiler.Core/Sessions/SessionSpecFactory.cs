using NetAndroidProfiler.Core.Apps;
using NetAndroidProfiler.Core.Projects;

namespace NetAndroidProfiler.Core.Sessions;

/// <summary>
/// Builds a <see cref="SessionSpec"/> from the loose, string-shaped input every frontend
/// receives (MCP arguments, HTTP JSON, a command line). The aliases and the validation
/// live here so the frontends cannot drift apart on what "heap" or "weaver" means.
/// </summary>
public static class SessionSpecFactory
{
    public static ProfilingMode ParseMode(string mode) => mode.Trim().ToLowerInvariant() switch
    {
        "sampling" or "cpu" => ProfilingMode.Sampling,
        "instrumenting" or "instrument" or "tracing" => ProfilingMode.Instrumenting,
        "heap" or "memory" or "gcdump" => ProfilingMode.HeapSnapshot,
        _ => throw new ProfilerException($"Unknown mode '{mode}': use sampling | instrumenting | heap."),
    };

    public static LaunchMode ParseLaunch(string launch) => launch.Trim().ToLowerInvariant() switch
    {
        "restart" or "" => LaunchMode.Restart,
        "attach" => LaunchMode.Attach,
        _ => throw new ProfilerException($"Unknown launch '{launch}': use restart | attach."),
    };

    public static InstrumentingEngine ParseEngine(string engine) => engine.Trim().ToLowerInvariant() switch
    {
        "provider" or "runtime" => InstrumentingEngine.RuntimeProvider,
        "auto" or "" => InstrumentingEngine.Auto,
        "weaver" or "cecil" or "il" => InstrumentingEngine.Weaver,
        "weaver-tree" or "tree" or "cct" => InstrumentingEngine.WeaverTree,
        _ => throw new ProfilerException($"Unknown engine '{engine}': use provider | weaver."),
    };

    /// <summary>Validate and assemble a spec; throws <see cref="ProfilerException"/> with user-facing guidance.</summary>
    public static SessionSpec Build(
        string deviceSerial,
        string packageName,
        string mode = "sampling",
        string launch = "restart",
        int? durationSeconds = null,
        string? callspec = null,
        bool trackAllocations = true,
        bool suspendOnStart = true,
        string? name = null,
        bool keepAppRunning = false,
        string engine = "auto",
        IReadOnlyList<string>? weaveAssemblies = null,
        IReadOnlyList<string>? weaveReferenceDirs = null,
        string? weaveMapPath = null,
        int snapshots = 1,
        int snapshotIntervalSeconds = 30,
        bool weavePropertyAccessors = false,
        bool weaveAsyncBodies = true,
        int maxTraceMb = 512,
        string? symbolsDir = null,
        string? projectPath = null,
        string? solutionPath = null,
        bool startPaused = false)
    {
        if (string.IsNullOrWhiteSpace(deviceSerial)) throw new ProfilerException("deviceSerial is required (list the devices first).");
        if (string.IsNullOrWhiteSpace(packageName)) throw new ProfilerException("packageName is required.");

        var pm = ParseMode(mode);
        var lm = ParseLaunch(launch);
        var eng = ParseEngine(engine);

        if (pm == ProfilingMode.Instrumenting && string.IsNullOrWhiteSpace(callspec) && string.IsNullOrWhiteSpace(weaveMapPath))
            throw new ProfilerException(
                "Instrumenting needs a callspec (e.g. N:My.App.Namespace). Instrumenting everything is not supported: it makes the app unusably slow.");

        // A session without symbols records no source locations, and nothing later can put
        // them back: the pdbs of that build are what they are read from. So they are looked
        // for rather than demanded - the project says where its build output is, and the
        // weaver's reference directories are that same folder by another name.
        string? symbols = FirstExisting(symbolsDir, OutputOf(projectPath), weaveReferenceDirs?.FirstOrDefault());

        return new SessionSpec(
            deviceSerial.Trim(),
            packageName.Trim(),
            pm,
            lm,
            durationSeconds is > 0 ? TimeSpan.FromSeconds(durationSeconds.Value) : null,
            // Suspending the app at launch waits for a diagnostic session, which is the very
            // thing a paused session defers: the two cannot both be true.
            suspendOnStart && !startPaused,
            string.IsNullOrWhiteSpace(callspec) ? null : callspec.Trim(),
            trackAllocations,
            string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            keepAppRunning,
            eng,
            weaveAssemblies is { Count: > 0 } ? weaveAssemblies : null,
            weaveReferenceDirs is { Count: > 0 } ? weaveReferenceDirs : null,
            string.IsNullOrWhiteSpace(weaveMapPath) ? null : weaveMapPath.Trim(),
            Math.Max(1, snapshots),
            TimeSpan.FromSeconds(Math.Max(1, snapshotIntervalSeconds)),
            weavePropertyAccessors,
            weaveAsyncBodies,
            maxTraceMb > 0 ? maxTraceMb * 1024L * 1024L : null,
            symbols,
            string.IsNullOrWhiteSpace(projectPath) ? null : projectPath.Trim(),
            string.IsNullOrWhiteSpace(solutionPath) ? null : solutionPath.Trim(),
            startPaused);
    }

    /// <summary>The build output of a project file, when it names one that exists.</summary>
    private static string? OutputOf(string? projectPath) =>
        string.IsNullOrWhiteSpace(projectPath) ? null : AppProjectFinder.Describe(projectPath.Trim())?.OutputDir;

    /// <summary>The first of these that is a directory on this machine; null when none is.</summary>
    private static string? FirstExisting(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && Directory.Exists(c.Trim()))?.Trim();

    /// <summary>Split a comma-separated frontend argument ("A,B") into a list, or null when empty.</summary>
    public static IReadOnlyList<string>? SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
