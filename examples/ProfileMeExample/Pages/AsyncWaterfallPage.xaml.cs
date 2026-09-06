using ProfileMeExample.Domain.Weather;
using ProfileMeExample.Scenarios;

namespace ProfileMeExample.Pages;

public partial class AsyncWaterfallPage : ScenarioPage
{
    private readonly ForecastService _forecasts = new();

    public AsyncWaterfallPage()
    {
        InitializeComponent();
        UseScenario(ScenarioCatalog.AsyncWaterfall);
    }

    private async void OnLoadClicked(object? sender, EventArgs e)
    {
        await RunAsync(LoadButton, OutputLabel, async () =>
        {
            var week = await _forecasts.GetWeekAsync(DateOnly.FromDateTime(DateTime.Today));
            return string.Join('\n', week.Select(day => $"  {day.Day:ddd dd}: {day.LowCelsius}-{day.HighCelsius} C, {day.Outlook}"));
        });
    }

    private async void OnGuideClicked(object? sender, EventArgs e) => await ShowGuideAsync();
}
