namespace ProfileMeExample.Domain.Inventory;

public sealed record StockLine(string Sku, string Warehouse, int OnHand, int ReorderPoint, int DailyDemand);

/// <summary>
/// Finds the stock lines that need reordering. Lazy on purpose: nothing runs until
/// somebody enumerates the result, and everything runs again each time somebody does.
/// </summary>
public sealed class StockQuery
{
    private readonly IReadOnlyList<StockLine> _lines;

    public StockQuery(IReadOnlyList<StockLine> lines) => _lines = lines;

    public IEnumerable<StockLine> LowStock()
    {
        foreach (var line in _lines)
        {
            if (NeedsReorder(line))
                yield return line;
        }
    }

    /// <summary>Projects the stock forward day by day until the reorder point is crossed.</summary>
    private static bool NeedsReorder(StockLine line)
    {
        var projected = (double)line.OnHand;
        for (var day = 0; day < 120; day++)
        {
            projected -= line.DailyDemand * (1 + Math.Sin(day / 7.0) * 0.2);
            if (projected <= line.ReorderPoint)
                return day < 30;
        }

        return false;
    }
}

/// <summary>
/// Summarizes what to reorder.
/// </summary>
/// <remarks>
/// Defect on purpose: <see cref="Build"/> enumerates the same lazy query three times -
/// <c>Any</c>, <c>Count</c>, <c>Sum</c> - so the projection runs three times over every
/// line. An instrumenting session shows <c>LowStock (iterator body)</c> called about
/// three times the number of low-stock lines and <c>NeedsReorder</c> called about three
/// times the number of stock lines. One <c>ToList()</c> would do the work once.
/// </remarks>
public sealed class ReorderReport
{
    public string Build(StockQuery query)
    {
        var low = query.LowStock();
        if (!low.Any())
            return "Nothing to reorder.";

        var lines = low.Count();
        var units = low.Sum(line => line.ReorderPoint - line.OnHand + line.DailyDemand * 30);
        return $"{lines} lines to reorder, {units} units in total.";
    }
}

public static class StockGenerator
{
    private static readonly string[] Warehouses = ["Milan", "Turin", "Bologna"];

    public static IReadOnlyList<StockLine> Generate(int count, int seed = 5)
    {
        var random = new Random(seed);
        var lines = new List<StockLine>(count);
        for (var i = 0; i < count; i++)
        {
            var demand = random.Next(1, 15);
            lines.Add(new StockLine($"SKU-{i:D5}", Warehouses[random.Next(Warehouses.Length)], random.Next(0, 600), random.Next(20, 120), demand));
        }

        return lines;
    }
}
