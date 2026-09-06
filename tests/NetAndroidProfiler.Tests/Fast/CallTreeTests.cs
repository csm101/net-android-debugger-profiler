using System.Diagnostics;
using NetAndroidProfiler.Collector;
using NetAndroidProfiler.Core.Analysis;
using NetAndroidProfiler.Core.Weaving;

namespace NetAndroidProfiler.Tests.Fast;

/// <summary>
/// The arithmetic the app does on every call, driven by hand: one node per call path,
/// inclusive time from enter to leave, exclusive time with the children taken out. Getting
/// this wrong is expensive to notice later - the numbers look plausible either way - so it
/// is checked with times chosen to make the right answer obvious.
/// </summary>
public class CallTreeTests
{
    // Method ids as the weaver hands them out.
    private const int Outer = 1, Inner = 2, Other = 3;

    [Fact]
    public void Inclusive_and_exclusive_time_split_between_a_caller_and_its_callee()
    {
        var tree = new CallTree(threadId: 7, generation: 0);
        tree.Enter(Outer, 1000);
        tree.Enter(Inner, 1100);
        tree.Leave(Inner, 1400);        // the callee took 300
        tree.Leave(Outer, 1500);        // the caller took 500, of which 300 were the callee

        var nodes = tree.Snapshot();
        Assert.Equal(2, nodes.Count);
        Assert.Equal(Outer, nodes.Method[0]);
        Assert.Equal(-1, nodes.Parent[0]);
        Assert.Equal(500, nodes.Inclusive[0]);
        Assert.Equal(200, nodes.Exclusive[0]);
        Assert.Equal(Inner, nodes.Method[1]);
        Assert.Equal(0, nodes.Parent[1]);
        Assert.Equal(300, nodes.Inclusive[1]);
        Assert.Equal(300, nodes.Exclusive[1]);
    }

    [Fact]
    public void The_same_method_under_two_callers_is_two_nodes()
    {
        var tree = new CallTree(1, 0);
        tree.Enter(Outer, 0); tree.Enter(Inner, 10); tree.Leave(Inner, 20); tree.Leave(Outer, 30);
        tree.Enter(Other, 100); tree.Enter(Inner, 110); tree.Leave(Inner, 150); tree.Leave(Other, 160);

        var nodes = tree.Snapshot();
        // Outer, Inner-under-Outer, Other, Inner-under-Other: the shape a call graph needs.
        Assert.Equal(4, nodes.Count);
        var innerNodes = Enumerable.Range(0, nodes.Count).Where(i => nodes.Method[i] == Inner).ToList();
        Assert.Equal(2, innerNodes.Count);
        Assert.Equal(10, nodes.Inclusive[innerNodes[0]]);
        Assert.Equal(40, nodes.Inclusive[innerNodes[1]]);
    }

    [Fact]
    public void Repeated_calls_accumulate_with_their_own_minimum_and_maximum()
    {
        var tree = new CallTree(1, 0);
        foreach (var (enter, leave) in new[] { (0L, 50L), (100L, 120L), (200L, 500L) })
        {
            tree.Enter(Outer, enter);
            tree.Leave(Outer, leave);
        }

        var nodes = tree.Snapshot();
        Assert.Equal(1, nodes.Count);
        Assert.Equal(3, nodes.Calls[0]);
        Assert.Equal(370, nodes.Inclusive[0]);
        Assert.Equal(20, nodes.Min[0]);
        Assert.Equal(300, nodes.Max[0]);
    }

    [Fact]
    public void A_leave_without_its_enter_is_ignored_rather_than_charged_to_the_wrong_node()
    {
        var tree = new CallTree(1, 0);
        tree.Leave(Outer, 100);                     // nothing was entered
        Assert.Equal(0, tree.Snapshot().Count);

        tree.Enter(Outer, 0);
        tree.Leave(Inner, 10);                      // a method that is not on top
        var nodes = tree.Snapshot();
        Assert.Equal(1, nodes.Count);
        Assert.Equal(0, nodes.Calls[0]);            // Outer is still open, nothing recorded
    }

    /// <summary>
    /// The nodes have to survive the trip out of the app: written by the collector, read by
    /// the analyzer, and turned into the same timings and tree the event stream produces.
    /// </summary>
    [Fact]
    public void Nodes_written_by_the_collector_become_timings_and_a_tree()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nap-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            long frequency = Stopwatch.Frequency;
            long ms = frequency / 1000;             // one millisecond in ticks
            WriteTree(Path.Combine(dir, "sample-t7-g0.napt"), frequency, threadId: 7,
            [
                (-1, Outer, 2, 10 * ms, 4 * ms, 4 * ms, 6 * ms),
                (0, Inner, 2, 6 * ms, 6 * ms, 2 * ms, 4 * ms),
            ]);

            var map = new List<WovenMethod>
            {
                new(Outer, "Sample", 0, "Sample.Work.Outer"),
                new(Inner, "Sample", 0, "Sample.Work.Inner"),
            };
            var result = new WeaveTreeAnalyzer().Analyze(dir, map);

            var outer = result.Timings.Single(t => result.Methods[t.MethodId].FullName.EndsWith("Outer"));
            Assert.Equal(2, outer.Calls);
            Assert.Equal(10_000_000, outer.TotalNs);        // 10 ms
            Assert.Equal(4_000_000, outer.SelfNs);
            Assert.Equal(4_000_000, outer.MinNs);
            Assert.Equal(6_000_000, outer.MaxNs);

            Assert.Equal(2, result.Tree.Count);
            var child = result.Tree.Single(n => n.ParentId is not null);
            Assert.Equal(result.Tree.Single(n => n.ParentId is null).Id, child.ParentId);
            Assert.Equal(1, child.Depth);
            Assert.Equal(7, child.ThreadId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void WriteTree(string path, long frequency, int threadId,
        (int Parent, int Method, long Calls, long Inclusive, long Exclusive, long Min, long Max)[] nodes)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(new[] { (byte)'N', (byte)'A', (byte)'P', (byte)'T' });
        writer.Write((byte)1);
        writer.Write(frequency);
        writer.Write(threadId);
        writer.Write(nodes.Length);
        foreach (var n in nodes)
        {
            writer.Write(n.Parent); writer.Write(n.Method);
            writer.Write(n.Calls); writer.Write(n.Inclusive); writer.Write(n.Exclusive);
            writer.Write(n.Min); writer.Write(n.Max);
        }
    }
}
