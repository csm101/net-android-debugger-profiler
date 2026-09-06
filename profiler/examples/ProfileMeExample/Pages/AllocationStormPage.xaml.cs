using ProfileMeExample.Domain.Reporting;
using ProfileMeExample.Scenarios;

namespace ProfileMeExample.Pages;

public partial class AllocationStormPage : ScenarioPage
{
    private const int SaleCount = 3000;

    private readonly SalesReportBuilder _builder = new();
    private readonly IReadOnlyList<Sale> _sales = SalesGenerator.Generate(SaleCount);

    public AllocationStormPage()
    {
        InitializeComponent();
        UseScenario(ScenarioCatalog.AllocationStorm);
    }

    private async void OnExportClicked(object? sender, EventArgs e)
    {
        await RunAsync(ExportButton, OutputLabel, () => Task.Run(() =>
        {
            var collectionsBefore = GC.CollectionCount(0);
            var allocatedBefore = GC.GetTotalAllocatedBytes();
            var csv = _builder.BuildCsv(_sales);
            var allocated = GC.GetTotalAllocatedBytes() - allocatedBefore;
            var collections = GC.CollectionCount(0) - collectionsBefore;
            return $"{SaleCount:N0} rows, {csv.Length:N0} characters of CSV.\nAllocated {allocated / (1024 * 1024):N0} MB along the way, {collections} gen-0 collections.";
        }));
    }

    private async void OnGuideClicked(object? sender, EventArgs e) => await ShowGuideAsync();
}
