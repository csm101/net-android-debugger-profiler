using NetAndroidProfiler.Core.Apps;
using NetAndroidProfiler.Core.Sessions;
using NetAndroidProfiler.Core.Symbols;

namespace NetAndroidProfiler.Tests.Device;

/// <summary>
/// Real-app sessions against the reference application (App.Droid) installed as a Debug build with
/// EnableDiagnostics on the profiler emulator. Opt-in: set NAP_REFAPP=1 (and
/// optionally NAP_REFAPP_SYMBOLS = App.Droid bin folder with the pdbs).
/// </summary>
[Trait("Category", "Device")]
[Trait("Category", "the reference application")]
public class ReferenceAppTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("NAP_REFAPP") == "1";
    private static string Serial => Environment.GetEnvironmentVariable("NAP_TEST_SERIAL") ?? "emulator-5556";
    private const string Package = "App.Droid";
    private static string SessionsRoot => Path.Combine(Path.GetTempPath(), "net-android-profiler-tests", "sessions");
    private static string SymbolsDir => Environment.GetEnvironmentVariable("NAP_REFAPP_SYMBOLS") ?? @"C:\Work\ReferenceApp\App.Droid\bin\Debug\net9.0-android35.0";

    private static async Task<ProfilerSession> RunAsync(SessionSpec spec)
    {
        var session = ProfilerSession.Create(spec, SessionsRoot);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        try { await session.RunAsync(cts.Token); }
        catch { Console.WriteLine(string.Join(Environment.NewLine, session.LogLines)); throw; }
        Console.WriteLine(string.Join(Environment.NewLine, session.LogLines.TakeLast(12)));
        return session;
    }

    [SkippableFact]
    public async Task Sampling_restart_session_on_the reference application_resolves_app_methods()
    {
        Skip.IfNot(Enabled, "set NAP_REFAPP=1 to run against the reference application");
        await using var s = await RunAsync(new SessionSpec(Serial, Package, ProfilingMode.Sampling, Duration: TimeSpan.FromSeconds(25)));
        Assert.Equal(SessionState.Ready, s.State);
        var row = s.Results.ReadSession()!;
        // First-ever launch produces ~12k samples (full IoC init); warm relaunches
        // sit mostly idle on the splash screen and yield far fewer.
        Assert.True(row.TotalSamples > 200, $"samples={row.TotalSamples}");
        var hot = s.Results.Hotspots(40, exclusive: false, cpuOnly: true);
        Console.WriteLine(string.Join(Environment.NewLine, hot.Select(h => $"{h.Inclusive,7} {h.Exclusive,7} {h.FullName}")));
        Assert.Contains(hot, h => h.FullName.StartsWith("V7.", StringComparison.Ordinal) || h.Module.StartsWith("V7", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(s.Results.Modules(), m => m.StartsWith("V7", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact(Skip = "TODO-RED: U20 - net9 MonoVM crashes at init when a profiler callspec is set (SIGSEGV registering the instrumentation filter callback); retest when the reference application targets net10. Weaver (P3) is the instrumenting path for net9.")]
    public async Task Instrumenting_session_on_the reference application_with_namespace_callspec()
    {
        Skip.IfNot(Enabled, "set NAP_REFAPP=1 to run against the reference application");
        await using var s = await RunAsync(new SessionSpec(Serial, Package, ProfilingMode.Instrumenting, Duration: TimeSpan.FromSeconds(25), Callspec: "N:App.Droid", TrackAllocations: true));
        Assert.Equal(SessionState.Ready, s.State);
        var timings = s.Results.Timings(30);
        Console.WriteLine(string.Join(Environment.NewLine, timings.Select(t => $"{t.Calls,8} {t.TotalNs / 1e6,10:F2}ms {t.FullName}")));
        Assert.NotEmpty(timings);
        Assert.All(timings, t => Assert.StartsWith("App.Droid", t.FullName));
        var allocs = s.Results.AllocationsByType(10);
        Console.WriteLine(string.Join(Environment.NewLine, allocs.Select(a => $"{a.Count,8} {a.Bytes,10} {a.TypeName}")));
        Assert.NotEmpty(allocs);
    }

    [SkippableFact]
    public async Task Weaver_instrumenting_session_on_the reference application()
    {
        Skip.IfNot(Enabled, "set NAP_REFAPP=1 to run against the reference application");
        string callspec = Environment.GetEnvironmentVariable("NAP_REFAPP_CALLSPEC") ?? "N:App.Droid";
        var asms = (Environment.GetEnvironmentVariable("NAP_REFAPP_WEAVE_ASMS") ?? "App.Droid").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        await using var s = await RunAsync(new SessionSpec(Serial, Package, ProfilingMode.Instrumenting,
            Duration: TimeSpan.FromSeconds(25),
            Callspec: callspec,
            Engine: InstrumentingEngine.Weaver,
            WeaveAssemblies: asms,
            WeaveReferenceDirs: [SymbolsDir]));
        Assert.Equal(SessionState.Ready, s.State);
        var timings = s.Results.Timings(40);
        Console.WriteLine(string.Join(Environment.NewLine, timings.Take(25).Select(t => $"{t.Calls,8} {t.TotalNs / 1e6,10:F2}ms {t.SelfNs / 1e6,10:F2}ms {t.FullName}")));
        Assert.NotEmpty(timings);
        Assert.All(timings, t => Assert.StartsWith("App.Droid", t.FullName));
        Assert.True(s.Results.Count("timing_tree") > 0);
    }

    /// <summary>
    /// U22 on the real app: the reference application keeps EmbedAssembliesIntoApk=true, so the app is
    /// woven during its own build and the session only consumes the map, touching
    /// nothing on the device. Needs a build with
    /// -p:NapWeave=true -p:NapCallspec="T:App.Droid.AppApplication".
    /// </summary>
    [SkippableFact]
    public async Task Build_time_weaving_session_on_the reference application()
    {
        string map = Environment.GetEnvironmentVariable("NAP_REFAPP_WEAVE_MAP")
            ?? Path.Combine(SymbolsDir, "nap-weave.map");
        Skip.IfNot(Enabled && File.Exists(map), $"build App.Droid with -p:NapWeave=true first (no map at {map})");

        await using var s = await RunAsync(new SessionSpec(Serial, Package, ProfilingMode.Instrumenting,
            Duration: TimeSpan.FromSeconds(12),
            Engine: InstrumentingEngine.Weaver,
            WeaveMapPath: map));
        Assert.Equal(SessionState.Ready, s.State);

        var timings = s.Results.Timings(20);
        Console.WriteLine(string.Join(Environment.NewLine, timings.Select(t => $"{t.Calls,4} calls {t.TotalNs / 1e6,10:F2} ms {t.SelfNs / 1e6,10:F2} self  {t.FullName}")));
        Assert.NotEmpty(timings);
        Assert.All(timings, t => Assert.StartsWith("App.Droid.", t.FullName));
        Assert.Contains(timings, t => t.FullName == "App.Droid.AppApplication.OnCreate");
    }

    [SkippableFact]
    public void the reference application_pdbs_load_and_map_tokens()
    {
        Skip.IfNot(Enabled && Directory.Exists(SymbolsDir), "set NAP_REFAPP=1 and build App.Droid Debug");
        using var pdbs = PortablePdbSymbols.LoadDirectory(SymbolsDir);
        Assert.Contains(pdbs.Modules, m => m.Equals("App.Droid", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pdbs.Modules, m => m.Equals("App.Core", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(pdbs.MethodsInDocument("AppApplication.cs"));
    }
}
