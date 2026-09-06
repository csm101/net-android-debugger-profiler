namespace ProfileMeExample.Domain.Catalog;

public sealed record SearchHit(Product Product, int Score);

/// <summary>
/// Fuzzy search the way it gets written the first time: every query re-tokenizes every
/// product, lower-cases every word again and runs a full edit distance against each one.
/// Fine for fifty products, a CPU hotspot for thousands.
/// </summary>
/// <remarks>
/// Defect on purpose: <see cref="Score"/> and <see cref="EditDistance"/> dominate the
/// exclusive CPU samples of the app while a search runs. The fix would be to tokenize and
/// lower-case once, at catalog load, and to reject words whose length rules out a match
/// before computing the distance.
/// </remarks>
public sealed class NaiveCatalogSearch
{
    private readonly IReadOnlyList<Product> _products;

    public NaiveCatalogSearch(IReadOnlyList<Product> products) => _products = products;

    public IReadOnlyList<SearchHit> Search(string query, int top = 20)
    {
        var hits = new List<SearchHit>();
        foreach (var product in _products)
        {
            var score = Score(product, query);
            if (score > 0)
                hits.Add(new SearchHit(product, score));
        }

        hits.Sort((a, b) => b.Score.CompareTo(a.Score));
        return hits.Count > top ? hits.GetRange(0, top) : hits;
    }

    private static int Score(Product product, string query)
    {
        var score = 0;
        foreach (var term in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var best = 0;
            foreach (var word in Tokenize(product))
            {
                var distance = EditDistance(word.ToLowerInvariant(), term.ToLowerInvariant());
                if (distance <= 2)
                    best = Math.Max(best, term.Length - distance);
            }

            score += best;
        }

        return score;
    }

    private static string[] Tokenize(Product product) =>
        (product.Name + " " + product.Description).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Levenshtein distance with a fresh matrix per call.</summary>
    private static int EditDistance(string a, string b)
    {
        var cost = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
            cost[i, 0] = i;
        for (var j = 0; j <= b.Length; j++)
            cost[0, j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = a[i - 1] == b[j - 1] ? 0 : 1;
                cost[i, j] = Math.Min(Math.Min(cost[i - 1, j] + 1, cost[i, j - 1] + 1), cost[i - 1, j - 1] + substitution);
            }
        }

        return cost[a.Length, b.Length];
    }
}
