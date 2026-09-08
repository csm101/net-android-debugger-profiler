namespace NetAndroidProfiler.Tests.Fast;

/// <summary>Paths of the checked-in recorded traces (copied to the output directory).</summary>
internal static class Recorded
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "recorded");
    public static string SamplingJit20s => Path.Combine(Dir, "testtarget-sampling-jit-20s.nettrace");
    public static string MonoProfiler4s => Path.Combine(Dir, "testtarget-monoprofiler-4s.nettrace");
    public static string HeapGcDump => Path.Combine(Dir, "testtarget-heap.gcdump");

    /// <summary>
    /// A Ready sampling session on disk, analysed from the recorded trace: what a frontend
    /// test needs when it wants results without a device.
    /// </summary>
    public static void PrepareSampledSession(string directory)
    {
        Directory.CreateDirectory(directory);
        var spec = new NetAndroidProfiler.Core.Sessions.SessionSpec("emulator-test", "recorded.test",
            NetAndroidProfiler.Core.Apps.ProfilingMode.Sampling);
        File.WriteAllText(Path.Combine(directory, "session.json"),
            System.Text.Json.JsonSerializer.Serialize(spec, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        var analysis = new NetAndroidProfiler.Core.Analysis.SamplingAnalyzer().Analyze(SamplingJit20s);
        using var store = NetAndroidProfiler.Core.Store.ResultStore.Create(Path.Combine(directory, "session.db"), "test");
        store.WriteSampling(analysis);
        store.WriteSession(new NetAndroidProfiler.Core.Store.SessionRow(Path.GetFileName(directory), "Sampling", "Ready",
            "recorded.test", "emulator-test", DateTimeOffset.UtcNow, 20000, "trace.nettrace",
            analysis.TotalSamples, analysis.SamplesWithStack, null, null));
    }

    public static string TempDb(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "net-android-profiler-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name + ".db");
    }
}
