namespace NetAndroidProfiler.Tests.Fast;

/// <summary>Paths of the checked-in recorded traces (copied to the output directory).</summary>
internal static class Recorded
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "recorded");
    public static string SamplingJit20s => Path.Combine(Dir, "testtarget-sampling-jit-20s.nettrace");
    public static string MonoProfiler4s => Path.Combine(Dir, "testtarget-monoprofiler-4s.nettrace");
    public static string HeapGcDump => Path.Combine(Dir, "testtarget-heap.gcdump");

    public static string TempDb(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "net-android-profiler-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name + ".db");
    }
}
