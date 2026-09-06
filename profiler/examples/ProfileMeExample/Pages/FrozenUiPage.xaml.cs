using ProfileMeExample.Domain.Sync;
using ProfileMeExample.Scenarios;

namespace ProfileMeExample.Pages;

public partial class FrozenUiPage : ScenarioPage
{
    private readonly LegacyGateway _gateway = new();
    private IDispatcherTimer? _ticker;
    private int _ticks;

    public FrozenUiPage()
    {
        InitializeComponent();
        UseScenario(ScenarioCatalog.FrozenUi);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ticker = Dispatcher.CreateTimer();
        _ticker.Interval = TimeSpan.FromMilliseconds(100);
        _ticker.Tick += (_, _) => TickerLabel.Text = $"UI ticks: {++_ticks}";
        _ticker.Start();
    }

    protected override void OnDisappearing()
    {
        _ticker?.Stop();
        base.OnDisappearing();
    }

    private async void OnFetchClicked(object? sender, EventArgs e)
    {
        // The blocking call is made on the UI thread on purpose: that is the defect.
        await RunAsync(FetchButton, OutputLabel, () =>
        {
            var balance = _gateway.FetchBalance();
            return Task.FromResult($"Balance: {balance.Amount:N2} {balance.Currency}");
        });
    }

    private async void OnGuideClicked(object? sender, EventArgs e) => await ShowGuideAsync();
}
