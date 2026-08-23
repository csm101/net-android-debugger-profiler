using NetAndroidProfiler.Core.Apps;

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
        string? symbolsDir = null)
    {
        if (string.IsNullOrWhiteSpace(deviceSerial)) throw new ProfilerException("deviceSerial is required (list the devices first).");
        if (string.IsNullOrWhiteSpace(packageName)) throw new ProfilerException("packageName is required.");

        var pm = ParseMode(mode);
        var lm = ParseLaunch(launch);
        var eng = ParseEngine(engine);

        if (pm == ProfilingMode.Instrumenting && string.IsNullOrWhiteSpace(callspec) && string.IsNullOrWhiteSpace(weaveMapPath))
            throw new ProfilerException(
                "Instrumenting needs a callspec (e.g. N:My.App.Namespace). Instrumenting everything is not supported: it makes the app unusably slow.");

        return new SessionSpec(
            deviceSerial.Trim(),
            packageName.Trim(),
            pm,
            lm,
            durationSeconds is > 0 ? TimeSpan.FromSeconds(durationSeconds.Value) : null,
            suspendOnStart,
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
            string.IsNullOrWhiteSpace(symbolsDir) ? null : symbolsDir.Trim());
    }

    /// <summary>Split a comma-separated frontend argument ("A,B") into a list, or null when empty.</summary>
    public static IReadOnlyList<string>? SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
