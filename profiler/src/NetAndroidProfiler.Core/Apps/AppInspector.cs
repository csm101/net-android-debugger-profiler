using System.IO.Compression;
using System.Text;
using NetAndroidProfiler.Core.Devices;

namespace NetAndroidProfiler.Core.Apps;

/// <summary>What the installed APK of a package allows, as far as profiling is concerned.</summary>
public sealed record AppPrerequisites(
    string Package,
    IReadOnlyList<string> ApkPaths,
    string Abi,
    bool IsDebuggable,
    bool HasDiagnosticsComponent,
    bool HasAotLibraries,
    bool HasMonoDiagnosticsBaked,
    IReadOnlyList<string> BakedEnvironmentHints,
    /// <summary>The app carries its assemblies inside the APK instead of the fast-deployment directory.</summary>
    bool HasAssemblyStore = false)
{
    /// <summary>Problems that block or degrade <paramref name="mode"/>; empty = ready. Each string is user guidance.</summary>
    public IReadOnlyList<PrerequisiteProblem> Check(ProfilingMode mode)
    {
        var list = new List<PrerequisiteProblem>();
        if (!HasDiagnosticsComponent)
            list.Add(new PrerequisiteProblem(true,
                $"The installed APK of {Package} has no diagnostics component (libmono-component-diagnostics_tracing.so). " +
                "Build the app with -p:EnableDiagnostics=true (see docs/APP_SETUP.md) and reinstall."));
        if (mode == ProfilingMode.Instrumenting)
        {
            if (HasAotLibraries)
                list.Add(new PrerequisiteProblem(true,
                    "The APK contains AOT-compiled assemblies (libaot-*.so): AOT methods are never instrumented. " +
                    "Build with -p:RunAOTCompilation=false for instrumenting sessions."));
            if (!IsDebuggable && !HasMonoDiagnosticsBaked)
                list.Add(new PrerequisiteProblem(true,
                    "Instrumenting a non-debuggable (Release) app requires MONO_DIAGNOSTICS baked into the APK environment " +
                    "(AndroidEnvironment file, see docs/APP_SETUP.md). On Debug builds the profiler injects it automatically."));
        }
        if (mode == ProfilingMode.Sampling && HasAotLibraries)
            list.Add(new PrerequisiteProblem(false,
                "The APK contains AOT-compiled assemblies: sampled stacks may miss leaf frames of AOT code (samples land on the caller). " +
                "Build with -p:RunAOTCompilation=false for exact attribution."));
        return list;
    }
}

/// <summary>A prerequisite failure (blocking) or degradation (warning) with user guidance.</summary>
public sealed record PrerequisiteProblem(bool IsBlocking, string Message);

/// <summary>Profiling modes a session can run.</summary>
public enum ProfilingMode
{
    /// <summary>CPU sampling via Microsoft-DotNETCore-SampleProfiler.</summary>
    Sampling,
    /// <summary>Enter/leave (+ allocations) via Microsoft-DotNETRuntimeMonoProfiler.</summary>
    Instrumenting,
    /// <summary>Live-heap snapshot by type (GC heap dump events).</summary>
    HeapSnapshot,
}

/// <summary>Inspects the installed APK(s) of a package on a device.</summary>
public sealed class AppInspector
{
    private readonly AdbClient _adb;

    public AppInspector(AdbClient adb) { _adb = adb; }

    public async Task<AppPrerequisites> InspectAsync(string serial, string package, string abi, CancellationToken ct)
    {
        var paths = await _adb.PackagePathsAsync(serial, package, ct).ConfigureAwait(false);
        if (paths.Count == 0)
            throw new ToolException($"Package {package} is not installed on {serial}");
        bool debuggable = await _adb.IsDebuggableAsync(serial, package, ct).ConfigureAwait(false);

        bool diag = false, aot = false, monoDiag = false, store = false;
        var hints = new List<string>();
        string tmp = Path.Combine(Path.GetTempPath(), "net-android-profiler", "apk", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            foreach (var remote in paths)
            {
                string local = Path.Combine(tmp, Path.GetFileName(remote));
                await _adb.PullAsync(serial, remote, local, ct).ConfigureAwait(false);
                using var zip = ZipFile.OpenRead(local);
                foreach (var e in zip.Entries)
                {
                    if (e.FullName.StartsWith("assemblies/", StringComparison.Ordinal)) store = true;
                    if (!e.FullName.StartsWith($"lib/{abi}/", StringComparison.Ordinal)) continue;
                    string name = Path.GetFileName(e.FullName);
                    if (name == "libmono-component-diagnostics_tracing.so") diag = true;
                    // Assemblies inside the APK appear either as an assembly store or as
                    // lib_<Assembly>.dll.so; without them the app is fast-deployed and its
                    // assemblies live in files/.__override__/<abi>/ on the device.
                    else if (name == "libassembly-store.so" || (name.StartsWith("lib_", StringComparison.Ordinal) && name.EndsWith(".dll.so", StringComparison.Ordinal)))
                        store = true;
                    else if (name.StartsWith("libaot-", StringComparison.Ordinal)) aot = true;
                    else if (name == "libxamarin-app.so")
                    {
                        using var s = e.Open();
                        using var ms = new MemoryStream();
                        await s.CopyToAsync(ms, ct).ConfigureAwait(false);
                        foreach (var str in AsciiStrings(ms.ToArray(), 12))
                        {
                            if (str.StartsWith("MONO_DIAGNOSTICS", StringComparison.Ordinal) || str.StartsWith("--diagnostic-mono-profiler", StringComparison.Ordinal))
                            { monoDiag = true; hints.Add(str); }
                            else if (str.StartsWith("DOTNET_DiagnosticPorts", StringComparison.Ordinal) || str.Contains(":9000,", StringComparison.Ordinal))
                                hints.Add(str);
                        }
                    }
                }
            }
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
        return new AppPrerequisites(package, paths, abi, debuggable, diag, aot, monoDiag, hints, store);
    }

    private static IEnumerable<string> AsciiStrings(byte[] data, int minLength)
    {
        var sb = new StringBuilder();
        foreach (byte b in data)
        {
            if (b >= 0x20 && b < 0x7F) { sb.Append((char)b); continue; }
            if (sb.Length >= minLength) yield return sb.ToString();
            sb.Clear();
        }
        if (sb.Length >= minLength) yield return sb.ToString();
    }
}
