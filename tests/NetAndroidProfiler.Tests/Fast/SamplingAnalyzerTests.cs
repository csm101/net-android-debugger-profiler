using NetAndroidProfiler.Core.Analysis;

namespace NetAndroidProfiler.Tests.Fast;

public class SamplingAnalyzerTests
{
    private static readonly Lazy<SamplingResult> Result = new(() => new SamplingAnalyzer().Analyze(Recorded.SamplingJit20s));

    [Fact]
    public void Busy_method_is_among_top_exclusive_cpu_hotspots()
    {
        var r = Result.Value;
        var top = r.Stats.OrderByDescending(s => s.ExclusiveCpu).Take(5).Select(s => r.Method(s.MethodId).FullName).ToList();
        Assert.Contains(top, n => n.Contains("CpuBurner.Busy"));
    }

    [Fact]
    public void Mix_leaf_method_is_visible_on_jit_build()
    {
        var r = Result.Value;
        var mix = r.Stats.Single(s => r.Method(s.MethodId).FullName.Contains("CpuBurner.Mix"));
        Assert.True(mix.Exclusive > 50, $"Mix exclusive samples = {mix.Exclusive}");
    }

    [Fact]
    public void Sleep_samples_are_classified_as_wait_and_excluded_from_cpu()
    {
        var r = Result.Value;
        var wait = r.Stats.Where(s => r.Method(s.MethodId).IsWaitFrame).Sum(s => s.Exclusive);
        Assert.True(wait > r.SamplesWithStack / 2, $"wait leaf samples = {wait} of {r.SamplesWithStack}");
        var sleep = r.Stats.Single(s => r.Method(s.MethodId).FullName.StartsWith("Interop.Sys.<LowLevelMonitor_TimedWait>"));
        Assert.Equal(0, sleep.ExclusiveCpu);
        Assert.True(sleep.Exclusive > 0);
    }

    [Fact]
    public void Workload_loop_is_inclusive_root_of_the_worker_thread()
    {
        var r = Result.Value;
        var loop = r.Stats.Single(s => r.Method(s.MethodId).FullName.Contains("WorkloadRunner.Loop"));
        Assert.True(loop.Inclusive >= r.SamplesWithStack * 95 / 100, $"Loop inclusive {loop.Inclusive} of {r.SamplesWithStack}");
        Assert.Equal(0, loop.Exclusive);
    }

    [Fact]
    public void Call_tree_has_busy_under_loop_and_mix_under_busy()
    {
        var r = Result.Value;
        var loopNode = r.Tree.Single(n => n.MethodId >= 0 && r.Method(n.MethodId).FullName.Contains("WorkloadRunner.Loop"));
        var busy = r.Tree.Single(n => n.ParentId == loopNode.Id && r.Method(n.MethodId).FullName.Contains("CpuBurner.Busy"));
        Assert.True(busy.Inclusive > 0);
        var mix = r.Tree.Single(n => n.ParentId == busy.Id && r.Method(n.MethodId).FullName.Contains("CpuBurner.Mix"));
        Assert.Equal(busy.Inclusive - busy.Exclusive, mix.Inclusive);
    }

    [Fact]
    public void Edges_link_loop_to_busy_and_busy_to_mix()
    {
        var r = Result.Value;
        int loop = r.Methods.Single(m => m.FullName.Contains("WorkloadRunner.Loop")).Id;
        int busy = r.Methods.Single(m => m.FullName.Contains("CpuBurner.Busy")).Id;
        int mix = r.Methods.Single(m => m.FullName.Contains("CpuBurner.Mix")).Id;
        Assert.Contains(r.Edges, e => e.CallerMethodId == loop && e.CalleeMethodId == busy);
        Assert.Contains(r.Edges, e => e.CallerMethodId == busy && e.CalleeMethodId == mix);
    }

    [Fact]
    public void Every_sample_with_stack_is_resolved()
    {
        var r = Result.Value;
        Assert.True(r.TotalSamples > 1000);
        Assert.True(r.SamplesWithStack > 1000);
        Assert.DoesNotContain(r.Methods, m => m.FullName.StartsWith('?'));
    }

    [Fact]
    public void Method_names_are_split_into_namespace_type_name()
    {
        var r = Result.Value;
        var busy = r.Methods.Single(m => m.FullName.Contains("CpuBurner.Busy"));
        Assert.Equal("TestTarget.Workloads", busy.Namespace);
        Assert.Equal("CpuBurner", busy.TypeName);
        Assert.Equal("Busy", busy.Name);
        Assert.Equal("(long)", busy.Signature);
        Assert.Equal("testtarget", busy.Module, ignoreCase: true);
    }

    [Fact]
    public void Threads_are_reported_with_sample_counts()
    {
        var r = Result.Value;
        Assert.True(r.Threads.Count >= 2);
        Assert.Equal(r.TotalSamples, r.Threads.Sum(t => t.Samples));
    }
}
