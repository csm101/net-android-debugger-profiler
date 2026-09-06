namespace ProfileMeExample.Domain.Catalog;

public sealed record Product(int Id, string Name, string Category, string Description, decimal Price);

/// <summary>
/// Deterministic fake catalog: the same seed always yields the same products, so two
/// profiles of the same screen can be compared.
/// </summary>
public static class ProductCatalog
{
    private static readonly string[] Adjectives =
        ["Compact", "Wireless", "Heavy-duty", "Portable", "Smart", "Classic", "Rugged", "Slim", "Industrial", "Eco"];

    private static readonly string[] Nouns =
        ["Drill", "Kettle", "Router", "Backpack", "Lantern", "Speaker", "Monitor", "Thermostat", "Tripod", "Blender", "Scanner", "Compressor"];

    private static readonly string[] Categories = ["Tools", "Kitchen", "Network", "Outdoor", "Audio", "Office"];

    private static readonly string[] Words =
    [
        "designed", "for", "daily", "use", "with", "a", "reinforced", "shell", "and", "long", "battery", "life",
        "ships", "in", "recycled", "packaging", "compatible", "with", "standard", "mounts", "rated", "outdoor",
    ];

    public static IReadOnlyList<Product> Generate(int count, int seed = 42)
    {
        var random = new Random(seed);
        var products = new List<Product>(count);
        for (var i = 0; i < count; i++)
        {
            var name = $"{Pick(Adjectives, random)} {Pick(Nouns, random)} {random.Next(100, 999)}";
            var description = string.Join(' ', Enumerable.Range(0, 12).Select(_ => Pick(Words, random)));
            var price = Math.Round((decimal)(random.NextDouble() * 400 + 5), 2);
            products.Add(new Product(i + 1, name, Pick(Categories, random), description, price));
        }

        return products;
    }

    private static string Pick(string[] values, Random random) => values[random.Next(values.Length)];
}
