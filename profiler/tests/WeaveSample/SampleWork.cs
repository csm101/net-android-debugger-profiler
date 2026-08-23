namespace WeaveSample;

/// <summary>Deterministic call shapes for weaver tests (recursion, nesting, exceptions, values).</summary>
public class SampleWork
{
    public int Calls { get; private set; }

    public long Fib(int n)
    {
        Calls++;
        return n <= 1 ? n : Fib(n - 1) + Fib(n - 2);
    }

    public string Compose(int n)
    {
        var parts = new List<string>();
        for (int i = 0; i < n; i++)
            parts.Add(Helper(i));
        return string.Join(",", parts);
    }

    private string Helper(int i) => (i * 2).ToString();

    public void Boom()
    {
        throw new InvalidOperationException("boom");
    }

    public int CatchAndReturn()
    {
        try { Boom(); return -1; }
        catch (InvalidOperationException) { return 42; }
    }

    public static double StaticWork(double x)
    {
        double acc = 0;
        for (int i = 1; i < 100; i++) acc += x / i;
        return acc;
    }
}

/// <summary>Property accessors and an async method, for the weaver's filtering rules.</summary>
public class Shapes
{
    public int Counter { get; set; }

    public int Doubled => Counter * 2;

    public async Task<int> AddAsync(int a, int b)
    {
        await Task.Yield();
        return a + b;
    }

    /// <summary>An iterator: everything below lives in a compiler-generated MoveNext.</summary>
    public IEnumerable<int> Squares(int count)
    {
        for (int i = 1; i <= count; i++)
            yield return i * i;
    }
}

/// <summary>
/// Generic shapes. A generic method has one metadata token whatever it is instantiated
/// with, so symbolication has to resolve Map&lt;int&gt; and Map&lt;string&gt; to the same source
/// range - and a method of a generic type has to resolve at all.
/// </summary>
public class Generic<T>
{
    public T? Last { get; private set; }

    public T Keep(T value)
    {
        Last = value;
        return value;
    }

    public IReadOnlyList<TOut> Map<TOut>(IEnumerable<T> source, Func<T, TOut> project)
    {
        var result = new List<TOut>();
        foreach (var item in source)
            result.Add(project(item));
        return result;
    }
}

/// <summary>Deterministic allocations for the weaver's allocation tracking.</summary>
public class Allocator
{
    public int MakeThings(int n)
    {
        var kept = new List<Thing>();      // 1 List + its internal array
        for (int i = 0; i < n; i++)
            kept.Add(new Thing(i));         // n Things
        var buffer = new byte[64];          // 1 byte[]
        return kept.Count + buffer.Length;
    }
}

/// <summary>Instances counted by the allocation test.</summary>
public class Thing
{
    public Thing(int id) => Id = id;
    public int Id { get; }
}

/// <summary>Type excluded by the test filter: must never be woven.</summary>
public class Untouched
{
    public int NotWoven() => 7;
}
