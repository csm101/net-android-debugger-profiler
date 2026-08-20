using System.Runtime.CompilerServices;

namespace TestTarget.Workloads;

/// <summary>Object retained in bulk so that a gcdump shows it as a dominant type.</summary>
public sealed class AllocHeavyRecord
{
    public long Id;
    public byte[] Payload = new byte[64];
    public string Label = string.Empty;
}

/// <summary>Allocation-heavy workload. Keeps a bounded live set so the heap stays large.</summary>
public sealed class AllocHog
{
    private const int LiveSetSize = 50_000;
    private readonly AllocHeavyRecord?[] _live = new AllocHeavyRecord?[LiveSetSize];
    private int _cursor;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Allocate(long iteration)
    {
        int created = 0;
        for (int i = 0; i < 20_000; i++)
        {
            var rec = NewRecord(iteration * 20_000 + i);
            _live[_cursor] = rec;
            _cursor = (_cursor + 1) % LiveSetSize;
            created++;
        }
        return created;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static AllocHeavyRecord NewRecord(long id)
    {
        return new AllocHeavyRecord { Id = id, Label = "rec" };
    }
}
