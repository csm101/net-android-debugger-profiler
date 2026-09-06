namespace ProfileMeExample.Domain.Pricing;

public sealed record OrderLine(string Sku, int Quantity, string Category);

public sealed record OrderSummary(int Lines, int Units, decimal Net, decimal Tax);

/// <summary>A price list looked up by SKU with a linear scan: cheap per call, as long as it is called once per line.</summary>
public sealed class PriceList
{
    private readonly List<(string Sku, decimal UnitPrice)> _prices;

    public PriceList(int skuCount, int seed = 7)
    {
        var random = new Random(seed);
        _prices = new List<(string, decimal)>(skuCount);
        for (var i = 0; i < skuCount; i++)
            _prices.Add((SkuFor(i), Math.Round((decimal)(random.NextDouble() * 90 + 1), 2)));
    }

    public static string SkuFor(int index) => $"SKU-{index:D5}";

    public decimal GetUnitPrice(string sku)
    {
        foreach (var entry in _prices)
        {
            if (string.Equals(entry.Sku, sku, StringComparison.OrdinalIgnoreCase))
                return entry.UnitPrice;
        }

        throw new KeyNotFoundException(sku);
    }
}

public static class TaxRules
{
    public static decimal RateFor(string category) => category switch
    {
        "Food" => 0.04m,
        "Books" => 0.04m,
        _ => 0.22m,
    };
}

/// <summary>
/// Totals an order. Every method here is fast; the screen is slow anyway.
/// </summary>
/// <remarks>
/// Defect on purpose: the price and the tax rate are looked up once per <em>unit</em>
/// instead of once per line, so a 2,000-line order performs tens of thousands of lookups.
/// Sampling points at <see cref="PriceList.GetUnitPrice"/> without saying why; an
/// instrumenting session shows the call count, which is the diagnosis.
/// </remarks>
public sealed class OrderSummaryBuilder
{
    private readonly PriceList _priceList;

    public OrderSummaryBuilder(PriceList priceList) => _priceList = priceList;

    public OrderSummary Build(IReadOnlyList<OrderLine> lines)
    {
        var units = 0;
        var net = 0m;
        var tax = 0m;
        foreach (var line in lines)
        {
            for (var unit = 0; unit < line.Quantity; unit++)
            {
                var price = _priceList.GetUnitPrice(line.Sku);
                net += price;
                tax += ApplyTax(price, line.Category);
                units++;
            }
        }

        return new OrderSummary(lines.Count, units, net, tax);
    }

    private static decimal ApplyTax(decimal price, string category) => price * TaxRules.RateFor(category);
}

public static class OrderGenerator
{
    private static readonly string[] Categories = ["Food", "Books", "Hardware", "Clothing"];

    public static IReadOnlyList<OrderLine> Generate(int lineCount, int skuCount, int seed = 11)
    {
        var random = new Random(seed);
        var lines = new List<OrderLine>(lineCount);
        for (var i = 0; i < lineCount; i++)
            lines.Add(new OrderLine(PriceList.SkuFor(random.Next(skuCount)), random.Next(1, 60), Categories[random.Next(Categories.Length)]));
        return lines;
    }
}
