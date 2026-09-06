using ProfileMeExample.Domain.Inventory;
using ProfileMeExample.Scenarios;

namespace ProfileMeExample.Pages;

public partial class ReenumeratedQueryPage : ScenarioPage
{
    private const int StockLines = 60000;

    private readonly StockQuery _query = new(StockGenerator.Generate(StockLines));
    private readonly ReorderReport _report = new();

    public ReenumeratedQueryPage()
    {
        InitializeComponent();
        UseScenario(ScenarioCatalog.ReenumeratedQuery);
    }

    private async void OnBuildClicked(object? sender, EventArgs e)
    {
        await RunAsync(BuildButton, OutputLabel, () => Task.Run(() => $"{StockLines:N0} stock lines. {_report.Build(_query)}"));
    }

    private async void OnGuideClicked(object? sender, EventArgs e) => await ShowGuideAsync();
}
