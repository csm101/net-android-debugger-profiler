using ProfileMeExample.Scenarios;

namespace ProfileMeExample.Pages;

/// <summary>
/// The guide behind "How to profile this": plain text for now. When the manual site
/// exists, <see cref="ScenarioInfo.ManualUrl"/> makes the button appear and opens the page.
/// </summary>
public partial class TutorialPage : ContentPage
{
    private readonly ScenarioInfo _scenario;

    public TutorialPage(ScenarioInfo scenario)
    {
        InitializeComponent();
        _scenario = scenario;
        TitleLabel.Text = scenario.Title;
        FeatureLabel.Text = scenario.Feature;
        GuideLabel.Text = scenario.Guide;
        ManualButton.IsVisible = scenario.ManualUrl is not null;
    }

    private async void OnOpenManualClicked(object? sender, EventArgs e)
    {
        if (_scenario.ManualUrl is not null)
            await Launcher.Default.OpenAsync(_scenario.ManualUrl);
    }

    private async void OnCloseClicked(object? sender, EventArgs e) => await Navigation.PopModalAsync();
}
