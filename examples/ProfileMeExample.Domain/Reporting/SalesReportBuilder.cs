namespace ProfileMeExample.Domain.Reporting;

public sealed record Sale(DateTime Day, string Region, string Sku, int Units, decimal Amount);

/// <summary>
/// Exports sales as CSV text.
/// </summary>
/// <remarks>
/// Defect on purpose: the report is built with <c>+=</c> on a string, so every row copies
/// the whole report so far, and each row boxes its numbers into a <c>List&lt;object&gt;</c>
/// before formatting. Nothing leaks, yet the GC runs constantly. An allocation report
/// attributes the strings to <see cref="BuildCsv"/> and the boxes and lists to
/// <see cref="FormatRow"/>.
/// </remarks>
public sealed class SalesReportBuilder
{
    public string BuildCsv(IReadOnlyList<Sale> sales)
    {
        var csv = "Day,Region,Sku,Units,Amount,RunningTotal\n";
        var running = 0m;
        foreach (var sale in sales)
        {
            running += sale.Amount;
            csv += FormatRow(sale, running);
        }

        return csv;
    }

    private static string FormatRow(Sale sale, decimal running)
    {
        var fields = new List<object> { sale.Day.ToString("yyyy-MM-dd"), sale.Region, sale.Sku, sale.Units, sale.Amount, running };
        return string.Join(",", fields.Select(field => field.ToString())) + "\n";
    }
}

public static class SalesGenerator
{
    private static readonly string[] Regions = ["North", "South", "East", "West"];

    public static IReadOnlyList<Sale> Generate(int count, int seed = 3)
    {
        var random = new Random(seed);
        var start = new DateTime(2026, 1, 1);
        var sales = new List<Sale>(count);
        for (var i = 0; i < count; i++)
        {
            var amount = Math.Round((decimal)(random.NextDouble() * 500 + 1), 2);
            sales.Add(new Sale(start.AddDays(i / 50), Regions[random.Next(Regions.Length)], $"SKU-{random.Next(1000):D4}", random.Next(1, 20), amount));
        }

        return sales;
    }
}
