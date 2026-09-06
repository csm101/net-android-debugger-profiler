using ProfileMeExample.Domain.Pricing;
using ProfileMeExample.Scenarios;

namespace ProfileMeExample.Pages;

public partial class ChattyPricingPage : ScenarioPage
{
    private const int LineCount = 2000;
    private const int SkuCount = 400;

    private readonly OrderSummaryBuilder _builder = new(new PriceList(SkuCount));
    private readonly IReadOnlyList<OrderLine> _order = OrderGenerator.Generate(LineCount, SkuCount);

    public ChattyPricingPage()
    {
        InitializeComponent();
        UseScenario(ScenarioCatalog.ChattyPricing);
    }

    private async void OnTotalClicked(object? sender, EventArgs e)
    {
        await RunAsync(TotalButton, OutputLabel, () => Task.Run(() =>
        {
            var summary = _builder.Build(_order);
            return $"{summary.Lines:N0} lines, {summary.Units:N0} units.\nNet {summary.Net:N2}, tax {summary.Tax:N2}.";
        }));
    }

    private async void OnGuideClicked(object? sender, EventArgs e) => await ShowGuideAsync();
}
