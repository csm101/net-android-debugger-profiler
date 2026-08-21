using NetAndroidProfiler.Core.Analysis;

namespace NetAndroidProfiler.Tests.Fast;

public class MonoProfilerAnalyzerTests
{
    private static readonly Lazy<InstrumentingResult> Result = new(() => new MonoProfilerAnalyzer().Analyze(Recorded.MonoProfiler4s));

    [Fact]
    public void Enter_leave_and_allocation_counts_match_the_recorded_trace()
    {
        var r = Result.Value;
        Assert.Equal(53592, r.EnterEvents);
        Assert.Equal(53589, r.LeaveEvents);
        Assert.Equal(53593, r.AllocationEvents);
    }

    [Fact]
    public void NewRecord_timing_has_expected_call_count_and_names_resolved()
    {
        var r = Result.Value;
        var t = r.Timings.Single(t => r.Method(t.MethodId).FullName == "TestTarget.Workloads.AllocHog.NewRecord");
        Assert.Equal(26793, t.Calls);
        Assert.True(t.TotalNs > 0 && t.SelfNs > 0 && t.SelfNs <= t.TotalNs);
        Assert.True(t.MinNs <= t.MaxNs);
        Assert.DoesNotContain(r.Timings, t => r.Method(t.MethodId).FullName.StartsWith("<unresolved"));
    }

    [Fact]
    public void Timing_tree_nests_ctor_under_NewRecord_under_Allocate()
    {
        var r = Result.Value;
        var allocate = r.Tree.Single(n => n.MethodId >= 0 && r.Method(n.MethodId).Name == "Allocate");
        var newRecord = r.Tree.Single(n => n.ParentId == allocate.Id && r.Method(n.MethodId).Name == "NewRecord");
        var ctor = r.Tree.Single(n => n.ParentId == newRecord.Id && r.Method(n.MethodId).Name == ".ctor");
        Assert.Equal(newRecord.Calls, ctor.Calls);
        Assert.True(newRecord.TotalNs >= ctor.TotalNs);
    }

    // The recorded trace is a second session on an already running process: no
    // ClassLoaded/VTableLoaded events, so every type is a "<vtable 0x...>"
    // placeholder (KNOWN_UNKNOWNS U13). Counts and sizes are still exact:
    // 26,794 AllocHeavyRecord (40 B) + 26,794 byte[64] (96 B) + a few others.

    [Fact]
    public void Allocations_by_type_count_every_record_and_its_payload()
    {
        var r = Result.Value;
        var rec = r.AllocsByType.Single(a => a.Count == 26794 && a.Bytes == 40 * 26794);
        var payload = r.AllocsByType.Single(a => a.Count >= 26794 && a.Bytes == 96 * a.Count);
        Assert.NotEqual(rec.TypeId, payload.TypeId);
        Assert.Equal(53593, r.AllocsByType.Sum(a => a.Count));
    }

    [Fact]
    public void Allocations_are_attributed_to_the_innermost_instrumented_frame()
    {
        var r = Result.Value;
        var rec = r.AllocsByType.Single(a => a.Count == 26794 && a.Bytes == 40 * 26794);
        var site = r.AllocsBySite.Where(a => a.TypeId == rec.TypeId).OrderByDescending(a => a.Count).First();
        Assert.Equal("TestTarget.Workloads.AllocHog.NewRecord", r.Method(site.MethodId).FullName);
        Assert.Equal(26794, site.Count);
    }

    /// <summary>
    /// A type whose vtable predates the session cannot be named: this runtime announces
    /// names only through ClassLoaded/VTableLoaded, and nothing replays them (U13 records
    /// what was tried). The allocations are still counted exactly, so the row must carry a
    /// label that says so instead of a bare pointer or an exception.
    /// </summary>
    [Fact]
    public void Types_that_predate_the_session_are_labelled_not_dropped()
    {
        var r = Result.Value;
        Assert.All(r.Types, t => Assert.StartsWith(MonoProfilerAnalyzer.UnresolvedTypePrefix, t.Name));
        Assert.All(r.Types, t => Assert.Contains("0x", t.Name));
        // The counts themselves are unaffected by the missing name.
        Assert.Contains(r.AllocsByType, a => a.Count > 0 && a.Bytes > 0);
    }
}
