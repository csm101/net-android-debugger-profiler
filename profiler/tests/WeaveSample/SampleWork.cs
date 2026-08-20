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

/// <summary>Type excluded by the test filter: must never be woven.</summary>
public class Untouched
{
    public int NotWoven() => 7;
}
