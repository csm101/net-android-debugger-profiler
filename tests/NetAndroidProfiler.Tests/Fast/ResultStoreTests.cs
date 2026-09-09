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

    /// <summary>
    /// A snapshot re-analyzes everything collected so far and rewrites the result tables:
    /// the database always holds the current picture (AQTime's Get Results is cumulative),
    /// while the segment table records when it was refreshed. Writing twice must therefore
    /// leave one set of rows, not two.
    /// </summary>
    [Fact]
    public void Snapshots_replace_the_results_and_leave_a_history()
    {
        var r = new SamplingAnalyzer().Analyze(Recorded.SamplingJit20s);
        string db = Recorded.TempDb("snapshots");
        using var w = ResultStore.Create(db, "test");

        w.WriteSampling(r);
        w.AddSegment(DateTimeOffset.UtcNow, "snapshot", r.TotalSamples, "first");
        long methodsAfterFirst = w.Count("method");
        long treeAfterFirst = w.Count("sample_tree");

        w.WriteSampling(r);                       // same data again: a second snapshot
        w.AddSegment(DateTimeOffset.UtcNow, "snapshot", r.TotalSamples, "second");

        Assert.Equal(methodsAfterFirst, w.Count("method"));
        Assert.Equal(treeAfterFirst, w.Count("sample_tree"));

        var segments = w.Segments();
        Assert.Equal(2, segments.Count);
        Assert.Equal("first", segments[0].note);
        Assert.All(segments, x => Assert.Equal("snapshot", x.kind));
    }

    [Fact]
    public void Clearing_empties_the_results_and_keeps_the_session_row()
    {
        var r = new SamplingAnalyzer().Analyze(Recorded.SamplingJit20s);
        string db = Recorded.TempDb("clear");
        using var w = ResultStore.Create(db, "test");
        w.WriteSession(new SessionRow("s1", "Sampling", "Collecting", "pkg", "serial", DateTimeOffset.UtcNow, null, null, null, null, null, null));
        w.WriteSampling(r);
        Assert.True(w.Count("sample_stat") > 0);

        w.ClearResults();
        w.AddSegment(DateTimeOffset.UtcNow, "clear", 0);

        foreach (string table in new[] { "sample_stat", "sample_tree", "sample_edge", "method", "thread" })
            Assert.Equal(0, w.Count(table));
        Assert.NotNull(w.ReadSession());                       // the session itself survives
        Assert.Contains(w.Segments(), x => x.kind == "clear"); // and so does the history
    }

    /// <summary>
    /// The pdbs are keyed by assembly name ("MyApp"), while a weave map records the file it
    /// rewrote ("MyApp.dll"): asking for one and storing the other found nothing, and the
    /// annotated source came out with no figures on it at all.
    /// </summary>
    [Fact]
    public void Figures_are_found_whichever_way_the_module_is_named()
    {
        var r = new MonoProfilerAnalyzer().Analyze(Recorded.MonoProfiler4s);
        string db = Recorded.TempDb("modulename");
        using (var w = ResultStore.Create(db, "test"))
        {
            w.WriteSession(new SessionRow("s3", "Instrumenting", "Ready", null, null, null, null, null, null, null, null, null));
            w.WriteInstrumenting(r with
            {
                Methods = r.Methods.Select(m => m with { Module = m.Module + ".dll" }).ToList(),
            });
        }
        using var s = ResultStore.Open(db);

        var asFile = s.MethodFiguresByModule("TestTarget.dll");
        var asAssembly = s.MethodFiguresByModule("TestTarget");

        Assert.NotEmpty(asFile);
        Assert.Equal(asFile.Count, asAssembly.Count);
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
    public void Heap_diff_reports_growth_and_disappearance()
    {
        string db = Recorded.TempDb("heapdiff");
        using (var w = ResultStore.Create(db, "test"))
        {
            w.WriteHeapSnapshot(DateTimeOffset.UtcNow, null, [("Leaking", 100, 4000), ("Stable", 10, 400), ("GoesAway", 5, 200)]);
            w.WriteHeapSnapshot(DateTimeOffset.UtcNow, null, [("Leaking", 900, 36000), ("Stable", 10, 400), ("NewType", 3, 120)]);
        }
        using var s = ResultStore.Open(db);
        var snapshots = s.HeapSnapshots();
        Assert.Equal(2, snapshots.Count);

        var diff = s.HeapDiff(snapshots[0].id, snapshots[1].id, 10);
        var leaking = diff.Single(d => d.TypeName == "Leaking");
        Assert.Equal(800, leaking.DeltaCount);
        Assert.Equal(32000, leaking.DeltaBytes);
        Assert.Equal("Leaking", diff[0].TypeName);          // ordered by bytes gained

        var stable = diff.Single(d => d.TypeName == "Stable");
        Assert.Equal(0, stable.DeltaCount);

        var gone = diff.Single(d => d.TypeName == "GoesAway");
        Assert.Equal(-5, gone.DeltaCount);
        Assert.Equal(0, gone.CountTo);

        var added = diff.Single(d => d.TypeName == "NewType");
        Assert.Equal(0, added.CountFrom);
        Assert.Equal(3, added.CountTo);
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

    /// <summary>
    /// A gcdump names the same type more than once (the same name comes back for distinct
    /// runtime types). Storing a row per occurrence broke the primary key and lost the whole
    /// session at its second snapshot; the figures of one type are the sum of its occurrences.
    /// </summary>
    [Fact]
    public void A_type_named_twice_in_a_snapshot_is_stored_once_with_the_totals()
    {
        string db = Recorded.TempDb("heap-duplicate-type");
        using var store = ResultStore.Create(db, "test");
        store.WriteHeapSnapshot(DateTimeOffset.UtcNow, null,
            [("System.String", 2, 100), ("System.String", 3, 50), ("System.Byte[]", 1, 8)]);
        store.WriteHeapSnapshot(DateTimeOffset.UtcNow, null, [("System.String", 1, 10)]);

        var rows = store.HeapByType(1, 10);

        var strings = rows.Single(r => r.typeName == "System.String");
        Assert.Equal(5, strings.count);
        Assert.Equal(150, strings.bytes);
        Assert.Equal(2, rows.Count);
    }
}
