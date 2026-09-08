using NetAndroidProfiler.Core.Sessions;

namespace NetAndroidProfiler.Core.Projects;

/// <summary>
/// What an app is being prepared for, translated into the build a session of that kind needs.
/// A frontend says one word instead of five properties: the mapping is knowledge that used to
/// live in a document, and it lives here so that every frontend gets the same build.
/// </summary>
public static class BuildPurpose
{
    /// <summary>Run under the debugger: a Debug build with fast deployment; diagnostics on, so the same build can be profiled in place.</summary>
    public const string Debug = "debug";

    /// <summary>CPU sampling.</summary>
    public const string Sampling = "sampling";

    /// <summary>Heap snapshots.</summary>
    public const string Heap = "heap";

    /// <summary>Instrumenting by weaving the deployed assemblies on the device.</summary>
    public const string Instrumenting = "instrumenting";

    /// <summary>Instrumenting for an app that keeps its assemblies inside the APK: woven while it is built.</summary>
    public const string InstrumentingBuildTime = "instrumenting-build-time";

    /// <summary>Every purpose, in the order a frontend lists them.</summary>
    public static readonly IReadOnlyList<string> All = [Debug, Sampling, Heap, Instrumenting, InstrumentingBuildTime];

    /// <summary>
    /// The build request for <paramref name="purpose"/>. Every on-device purpose is the same
    /// build (Debug, diagnostics, fast deployment), which is what lets the debugger and the
    /// profiler share one installed app; build-time weaving keeps the assemblies embedded and
    /// needs the callspec that is baked in.
    /// </summary>
    public static AppBuildRequest ToRequest(
        string purpose,
        string projectPath,
        string? deviceSerial = null,
        string? configuration = null,
        string? callspec = null,
        IReadOnlyList<string>? weaveAssemblies = null,
        bool clearDeployedAssemblies = false,
        string? packageName = null,
        bool install = true)
    {
        var config = string.IsNullOrWhiteSpace(configuration) ? "Debug" : configuration.Trim();
        switch ((purpose ?? "").Trim().ToLowerInvariant())
        {
            case Debug:
            case Sampling:
            case Heap:
            case Instrumenting:
                return new AppBuildRequest(projectPath, config, deviceSerial,
                    EnableDiagnostics: true, FastDeployment: true, Install: install,
                    ClearDeployedAssemblies: clearDeployedAssemblies, PackageName: packageName);
            case InstrumentingBuildTime:
                if (string.IsNullOrWhiteSpace(callspec))
                    throw new ProfilerException(
                        $"Purpose {InstrumentingBuildTime} needs a callspec: the instrumentation is baked into the app, " +
                        "and weaving every method makes it unusably slow. Name the namespace or type to time, e.g. N:My.App.Services.");
                return new AppBuildRequest(projectPath, config, deviceSerial,
                    EnableDiagnostics: true, FastDeployment: false, Install: install,
                    ClearDeployedAssemblies: clearDeployedAssemblies, PackageName: packageName,
                    Weave: true, Callspec: callspec, WeaveAssemblies: weaveAssemblies);
            default:
                throw new ProfilerException($"Unknown purpose '{purpose}'. One of: {string.Join(", ", All)}.");
        }
    }
}
