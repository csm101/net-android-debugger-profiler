using System.Diagnostics;
using NetAndroidProfiler.Core.Analysis;
using NetAndroidProfiler.Core.Store;

namespace NetAndroidProfiler.Tests.Fast;

/// <summary>
/// U6: the SQLite database is the GUI contract, and a real app produces call trees
/// with hundreds of thousands of nodes. This writes a synthetic result of that size
/// and checks that the queries a GUI runs per keystroke stay interactive. The
/// thresholds are deliberately loose (they guard against an accidental O(n) scan or
/// a missing index, not against a slow machine).
/// </summary>
public class ResultStoreScaleTests
{
    private const int Methods = 5_000;
    private const int TreeNodes = 200_000;

    [Fact]
    public void Large_call_tree_stays_queryable()
    {
        var result = SyntheticSampling(Methods, TreeNodes);
        string db = Recorded.TempDb("scale");

        var writeWatch = Stopwatch.StartNew();
        using (var w = ResultStore.Create(db, "test"))
        {
            w.WriteSampling(result);
            w.WriteSession(new SessionRow("scale", "Sampling", "Ready", null, null, null, null, null, result.TotalSamples, result.SamplesWithStack, null, null));
        }
        writeWatch.Stop();

        using var s = ResultStore.Open(db);
        Assert.Equal(TreeNodes + 1, s.Count("sample_tree"));   // + the thread root

        // The three queries a call-tree UI runs constantly.
        var hotspots = Measure(() => s.Hotspots(50));
        var roots = Measure(() => s.SampleTreeChildren(null));
        var children = Measure(() => s.SampleTreeChildren(roots.value[0].Id, 100));
        var callers = Measure(() => s.Callers(1, 50));

        Assert.NotEmpty(hotspots.value);
        Assert.NotEmpty(children.value);
        Assert.True(hotspots.ms < 500, $"hotspots took {hotspots.ms} ms");
        Assert.True(roots.ms < 500, $"roots took {roots.ms} ms");
        Assert.True(children.ms < 500, $"expanding a node took {children.ms} ms");
        Assert.True(callers.ms < 500, $"callers took {callers.ms} ms");
        Assert.True(writeWatch.Elapsed < TimeSpan.FromMinutes(2), $"writing {TreeNodes} nodes took {writeWatch.Elapsed}");
    }

    private static (T value, double ms) Measure<T>(Func<T> query)
    {
        var watch = Stopwatch.StartNew();
        var value = query();
        watch.Stop();
        return (value, watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>A wide, shallow tree with a realistic method count, like a real app's profile.</summary>
    private static SamplingResult SyntheticSampling(int methods, int nodes)
    {
        var methodRecords = Enumerable.Range(0, methods)
            .Select(i => new MethodRecord(i, "Synthetic", "Synthetic.Ns", $"Type{i % 200}", $"Method{i}", "()", $"Synthetic.Ns.Type{i % 200}.Method{i}", (ulong)i, false, 0x06000000 | i))
            .ToList();
        var stats = methodRecords
            .Select(m => new SampleStat(m.Id, methods - m.Id, (methods - m.Id) / 2, methods - m.Id, (methods - m.Id) / 2))
            .ToList();

        var tree = new List<SampleTreeNode> { new(0, null, -1, 1, 0, nodes, 0, nodes, 0) };
        var random = new Random(42);
        for (int i = 1; i <= nodes; i++)
        {
            int parent = i < 100 ? 0 : random.Next(0, i / 2);   // wide near the root, deep in places
            int depth = parent == 0 ? 1 : tree[parent].Depth + 1;
            tree.Add(new SampleTreeNode(i, parent, i % methods, 1, depth, 10, 5, 10, 5));
        }
        var edges = Enumerable.Range(1, Math.Min(methods - 1, 4_000))
            .Select(i => new CallEdge(i - 1, i, i))
            .ToList();

        return new SamplingResult(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), nodes, nodes,
            methodRecords, [new ThreadRecord(1, 1, "main", nodes, 0, 30_000)], stats, tree, edges);
    }
}
