using NetAndroidProfiler.Core.Devices;
using NetAndroidProfiler.Core.Sessions;

namespace NetAndroidProfiler.Core.Projects;

/// <summary>
/// Builds and installs an application project the way a profiling session needs it, so a
/// frontend that knows the sources can prepare the app without sending the user to a
/// command prompt. The profiler still never rebuilds anything on its own: this runs only
/// when a frontend asks for it, and the session that follows profiles whatever is
/// installed, exactly as before.
/// </summary>
public static class AppBuilder
{
    /// <summary>The msbuild command line a request produces, separate from running it so it can be shown and tested.</summary>
    public static IReadOnlyList<string> ArgumentsFor(AppBuildRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectPath))
            throw new ProfilerException("A project path is required.");
        if (!File.Exists(request.ProjectPath))
            throw new ProfilerException($"No such project: {request.ProjectPath}");

        var args = new List<string>
        {
            "build",
            Path.GetFullPath(request.ProjectPath),
            "-c", string.IsNullOrWhiteSpace(request.Configuration) ? "Debug" : request.Configuration,
        };
        if (request.Install) args.Add("-t:Install");
        if (request.EnableDiagnostics) args.Add("-p:EnableDiagnostics=true");
        // Fast deployment is what lets the weaver rewrite the app's assemblies on the
        // device; without it an instrumenting session records nothing unless the app was
        // woven while it was built (docs/APP_SETUP.md). Both are said explicitly: an app
        // asked to keep its assemblies inside the APK must not get the project's default.
        args.Add(request.FastDeployment ? "-p:EmbedAssembliesIntoApk=false" : "-p:EmbedAssembliesIntoApk=true");

        if (request.Weave)
        {
            if (string.IsNullOrWhiteSpace(request.Callspec))
                throw new ProfilerException(
                    "Weaving during the build needs a callspec: it is baked into the app, and weaving " +
                    "everything makes it unusably slow.");
            string targets = request.WeavingTargets ?? ToolLocator.FindWeavingTargets()
                ?? throw new ProfilerException(
                    "NetAndroidProfiler.Weaving.targets was not found: it ships in the package's build/ " +
                    "folder, next to bin/. Keep the package together, or pass its path.");
            args.Add("-p:NapWeave=true");
            // An app is more than its own assembly: the business logic usually lives in a
            // library, and without these the build instruments only the application project -
            // so a callspec pointing at the logic collects nothing.
            if (request.WeaveAssemblies is { Count: > 0 })
                // A semicolon separates properties on an msbuild command line, exactly as a comma
                // does, so a list of assemblies has to arrive escaped or msbuild reads the second
                // name as another property and refuses the lot.
                args.Add("-p:NapAssemblies=" + string.Join("%3B", request.WeaveAssemblies));
            // A comma separates properties on an msbuild command line, and a callspec is
            // allowed to contain them.
            args.Add("-p:NapCallspec=" + request.Callspec.Replace(",", "%2C"));
            // Handed to the build rather than imported by the app project: instrumenting
            // an app must not require editing it.
            args.Add("-p:CustomAfterMicrosoftCommonTargets=" + Path.GetFullPath(targets));

            // The targets default both of these to the package's build\tools, which is right
            // for an installation and empty in a source tree until somebody publishes. Saying
            // where they are is this side's job: it is the side that knows.
            if (ToolLocator.FindWeaveTool() is { } weaver)
                args.Add("-p:NapWeaveTool=" + weaver);
            if (ToolLocator.FindCollectorAssembly() is { } collector)
                args.Add("-p:NapCollectorAssembly=" + collector);
        }
        if (!string.IsNullOrWhiteSpace(request.DeviceSerial))
            args.Add($"-p:AdbTarget=-s {request.DeviceSerial}");
        args.Add("-nologo");
        args.Add("-v:m");
        return args;
    }

    /// <summary>
    /// Runs the build, streaming msbuild's output line by line, and answers its exit code.
    /// A failing build is not an exception: its output is the diagnosis, and the caller
    /// shows it.
    /// </summary>
    public static async Task<int> RunAsync(
        AppBuildRequest request, Action<string> log, CancellationToken ct)
    {
        var args = ArgumentsFor(request);
        if (request.ClearDeployedAssemblies) await ClearOverridesAsync(request, log, ct).ConfigureAwait(false);
        string dotnet = ToolLocator.FindDotnet()
            ?? throw new ToolException("dotnet was not found on this machine: install the .NET SDK, or put dotnet on PATH.");
        log($"{dotnet} {string.Join(' ', args)}");
        var result = await ProcessRunner.RunStreamingAsync(dotnet, args, log, ct).ConfigureAwait(false);
        log(result == 0
            ? "Build succeeded."
            : $"Build failed with exit code {result}.");
        return result;
    }

    /// <summary>
    /// Removes the assemblies a previous fast deployment left in the app's sandbox.
    /// Switching an app from embedded assemblies to fast deployment leaves the old copies
    /// in <c>files/.__override__</c>, and the runtime prefers whatever is there: stale
    /// assemblies can stop the app from starting at all (docs/APP_SETUP.md). The build
    /// that follows deploys them again, so this costs nothing but a redeploy - and unlike
    /// uninstalling, it leaves the app's data alone.
    /// </summary>
    private static async Task ClearOverridesAsync(AppBuildRequest request, Action<string> log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PackageName) || string.IsNullOrWhiteSpace(request.DeviceSerial))
        {
            log("Not clearing the deployed assemblies: that needs both the package and the device.");
            return;
        }
        try
        {
            var adb = new AdbClient();
            if (!await adb.IsDebuggableAsync(request.DeviceSerial, request.PackageName, ct).ConfigureAwait(false))
            {
                log($"Not clearing the deployed assemblies: {request.PackageName} is not installed as a debuggable app.");
                return;
            }
            await adb.RunAsAsync(request.DeviceSerial, request.PackageName, "rm -rf files/.__override__", ct).ConfigureAwait(false);
            log($"Cleared files/.__override__ of {request.PackageName} on {request.DeviceSerial}.");
        }
        catch (ToolException e)
        {
            // Not fatal: the build below reinstalls the app anyway, and an app that has
            // nothing to clear is the common case.
            log("Could not clear the deployed assemblies: " + e.Message);
        }
    }
}

/// <summary>What to build, and with which of the profiling prerequisites turned on.</summary>
/// <param name="ProjectPath">The application .csproj to build.</param>
/// <param name="Configuration">Build configuration; Debug is the one every mode works on.</param>
/// <param name="DeviceSerial">Device to install on; null installs on the only attached one.</param>
/// <param name="EnableDiagnostics">Add -p:EnableDiagnostics=true, the one property every mode needs.</param>
/// <param name="FastDeployment">Add -p:EmbedAssembliesIntoApk=false, which the weaver engines need.</param>
/// <param name="Install">Deploy to the device (-t:Install) rather than only building.</param>
/// <param name="ClearDeployedAssemblies">Delete the app's fast-deployment directory before building, for an app that is changing deployment mode.</param>
/// <param name="PackageName">The app's package; only needed to clear its deployed assemblies.</param>
/// <param name="Weave">Instrument the app while it is built, for an app that keeps its assemblies inside the APK.</param>
/// <param name="Callspec">Which methods that weave covers; required when <paramref name="Weave"/> is set.</param>
/// <param name="WeavingTargets">Path of NetAndroidProfiler.Weaving.targets; found next to the installation when omitted.</param>
public sealed record AppBuildRequest(
    string ProjectPath,
    string Configuration = "Debug",
    string? DeviceSerial = null,
    bool EnableDiagnostics = true,
    bool FastDeployment = true,
    bool Install = true,
    bool ClearDeployedAssemblies = false,
    string? PackageName = null,
    bool Weave = false,
    string? Callspec = null,
    string? WeavingTargets = null,
    IReadOnlyList<string>? WeaveAssemblies = null);
