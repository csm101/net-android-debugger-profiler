using NetAndroidProfiler.Core.Analysis;
using NetAndroidProfiler.Core.Store;

namespace NetAndroidProfiler.Tests.Fast;

public class ResultStoreTests
{
    [Fact]
    public void Sampling_round_trip_hotspots_tree_and_edges()
    {
        var r = new SamplingAnalyzer().Analyze(Recorded.SamplingJit20s);
        string db = Recorded.TempDb("sampling");
        using (var w = ResultStore.Create(db, "test"))
        {
            w.WriteSession(new SessionRow("s1", "Sampling", "Ready", "com.mcasoftware.testtarget", "emulator-5556", DateTimeOffset.UtcNow, 20000, "x.nettrace", r.TotalSamples, r.SamplesWithStack, null, null));
            w.WriteSampling(r);
        }
        using var s = ResultStore.Open(db);
        Assert.Equal("Sampling", s.ReadSession()!.Mode);
        var hot = s.Hotspots(5, exclusive: true, cpuOnly: true);
        Assert.Contains(hot, h => h.FullName.Contains("CpuBurner.Busy"));
        Assert.DoesNotContain(hot, h => h.IsWaitFrame);
        var hotWall = s.Hotspots(3, exclusive: true, cpuOnly: false);
        Assert.Contains(hotWall, h => h.IsWaitFrame);

        var roots = s.SampleTreeChildren(null);
        Assert.True(roots.Count >= 1);
        var worker = roots.OrderByDescending(x => x.Value1).First();
        var children = s.SampleTreeChildren(worker.Id);
        Assert.True(children.Count >= 1);

        int busy = s.FindMethodId("CpuBurner.Busy")!.Value;
        Assert.Contains(s.Callees(busy), e => e.FullName.Contains("CpuBurner.Mix"));
        Assert.Contains(s.Callers(busy), e => e.FullName.Contains("WorkloadRunner.Loop"));
        Assert.Equal(r.Methods.Count, s.Count("method"));
        Assert.Equal(r.Tree.Count, s.Count("sample_tree"));
    }

    [Fact]
    public void Instrumenting_round_trip_timings_and_allocations()
    {
        var r = new MonoProfilerAnalyzer().Analyze(Recorded.MonoProfiler4s);
        string db = Recorded.TempDb("instr");
        using (var w = ResultStore.Create(db, "test"))
        {
            w.WriteSession(new SessionRow("s2", "Instrumenting", "Ready", null, null, null, null, null, null, null, null, null));
            w.WriteInstrumenting(r);
        }
        using var s = ResultStore.Open(db);
        var timings = s.Timings(10);
        Assert.Contains(timings, t => t.FullName == "TestTarget.Workloads.AllocHog.NewRecord" && t.Calls == 26793);
        var allocs = s.AllocationsByType(10);
        Assert.Contains(allocs, a => a.Count == 26794 && a.Bytes == 40 * 26794); // AllocHeavyRecord (name unresolved in this trace, U13)
        var sites = s.AllocationsBySite(10);
        Assert.Contains(sites, a => a.MethodFullName.EndsWith("NewRecord"));
        var roots = s.TimingTreeChildren(null);
        Assert.Single(roots);
    }

    [Fact]
    public void Heap_snapshot_round_trip()
    {
        string db = Recorded.TempDb("heap");
        using (var w = ResultStore.Create(db, "test"))
        {
            int id = w.WriteHeapSnapshot(DateTimeOffset.UtcNow, null, [("A.B", 10, 400), ("System.String", 5, 120)]);
            int id2 = w.WriteHeapSnapshot(DateTimeOffset.UtcNow, null, [("A.B", 12, 480)]);
            Assert.NotEqual(id, id2);
        }
        using var s = ResultStore.Open(db);
        Assert.Equal(2, s.Count("type"));
        Assert.Equal(2, s.Count("heap_snapshot"));
        var rows = s.HeapByType(1, 10);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void Open_rejects_wrong_schema_version()
    {
        string db = Recorded.TempDb("bad");
        using (var w = ResultStore.Create(db, "test")) { }
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE schema_info SET version = 99";
            cmd.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => ResultStore.Open(db));
    }
}
