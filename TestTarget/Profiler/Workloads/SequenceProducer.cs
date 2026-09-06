namespace TestTarget.Workloads;

/// <summary>
/// An iterator whose body lives in a compiler-generated MoveNext, so the profiler's
/// weaver has a real "(iterator body)" to instrument on the device: one resumption per
/// item produced, plus the one that ends the sequence.
/// </summary>
public sealed class SequenceProducer
{
    public const int ItemsPerIteration = 25;

    /// <summary>Numbers whose production costs a little work each, so the body has a duration.</summary>
    public IEnumerable<long> Fibonacci(int count)
    {
        long a = 0, b = 1;
        for (int i = 0; i < count; i++)
        {
            (a, b) = (b, a + b);
            yield return a;
        }
    }

    /// <summary>Consume the sequence; returns a value so nothing can be optimized away.</summary>
    public long Consume(int count)
    {
        long sum = 0;
        foreach (long value in Fibonacci(count)) sum += value % 1_000_003;
        return sum;
    }
}
