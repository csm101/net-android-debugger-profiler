using ProfileMeExample.Scenarios;

namespace ProfileMeExample.Pages;

public partial class HomePage : ContentPage
{
    public HomePage()
    {
        InitializeComponent();
        ScenarioList.ItemsSource = ScenarioCatalog.All;
    }

    private async void OnScenarioSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not ScenarioInfo scenario)
            return;

        ScenarioList.SelectedItem = null;
        await Shell.Current.GoToAsync(scenario.Route);
    }
}
