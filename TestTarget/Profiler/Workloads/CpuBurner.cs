using System.Runtime.CompilerServices;

namespace TestTarget.Workloads;

/// <summary>CPU-bound workload. <see cref="Busy"/> must show up as a sampling hotspot.</summary>
public sealed class CpuBurner
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public long Busy(long seed)
    {
        long acc = seed;
        for (int i = 0; i < 2_000_000; i++)
        {
            acc = Mix(acc, i);
        }
        return acc;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Mix(long acc, int i)
    {
        acc ^= (acc << 13);
        acc ^= (acc >> 7);
        acc ^= (acc << 17);
        return acc + i;
    }
}
