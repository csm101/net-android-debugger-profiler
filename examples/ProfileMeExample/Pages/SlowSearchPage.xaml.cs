using ProfileMeExample.Domain.Catalog;
using ProfileMeExample.Scenarios;

namespace ProfileMeExample.Pages;

public partial class SlowSearchPage : ScenarioPage
{
    // 8,000 products answer in under a second on an x86_64 emulator; this size takes seconds, as the screen promises.
    private const int CatalogSize = 30000;

    private readonly NaiveCatalogSearch _search = new(ProductCatalog.Generate(CatalogSize));

    public SlowSearchPage()
    {
        InitializeComponent();
        UseScenario(ScenarioCatalog.SlowSearch);
    }

    private async void OnSearchClicked(object? sender, EventArgs e)
    {
        var query = QueryEntry.Text ?? string.Empty;
        await RunAsync(SearchButton, OutputLabel, () => Task.Run(() =>
        {
            var hits = _search.Search(query);
            var listing = string.Join('\n', hits.Take(5).Select(hit => $"  {hit.Product.Name} (score {hit.Score})"));
            return $"{hits.Count} hits in a catalog of {CatalogSize:N0} products.\n{listing}";
        }));
    }

    private async void OnGuideClicked(object? sender, EventArgs e) => await ShowGuideAsync();
}
