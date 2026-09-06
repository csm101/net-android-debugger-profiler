using System.Runtime.CompilerServices;

namespace TestTarget.Workloads;

/// <summary>
/// Controlled workload for characterizing how the MonoVM sample profiler
/// attributes leaf frames (KNOWN_UNKNOWNS U15).
///
/// Two shapes with the same total CPU cost per cycle:
/// <see cref="LongLeaf"/> is a single call that spins for a long time, while
/// <see cref="TinyLeaf"/> is called in a tight loop by <see cref="CallTinyLeaf"/>.
/// If the sampler reports true leaves, both appear with exclusive samples; if it
/// reports the last frame with a stack-walk anchor, only the long one does.
/// </summary>
public sealed class LeafProbe
{
    private readonly Thread _thread;
    private long _sink;

    private LeafProbe()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "TestTargetLeafProbe" };
    }

    public static LeafProbe Start()
    {
        var p = new LeafProbe();
        p._thread.Start();
        return p;
    }

    private void Loop()
    {
        while (true)
        {
            _sink += LongLeaf(120);      // one call, long body
            _sink += CallTinyLeaf(2000); // many calls, tiny body
            Thread.Sleep(50);
        }
    }

    /// <summary>One call that stays inside this method for a long time.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public long LongLeaf(int rounds)
    {
        long acc = 1;
        for (int r = 0; r < rounds; r++)
            for (int i = 1; i < 20_000; i++)
                acc = acc * 31 + i;
        return acc;
    }

    /// <summary>Calls a tiny leaf method in a tight loop.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public long CallTinyLeaf(int rounds)
    {
        long acc = 0;
        for (int r = 0; r < rounds; r++)
            for (int i = 0; i < 1000; i++)
                acc += TinyLeaf(acc, i);
        return acc;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long TinyLeaf(long acc, int i) => acc ^ (i * 2654435761L);
}
