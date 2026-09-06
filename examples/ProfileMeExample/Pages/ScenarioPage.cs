using System.Diagnostics;
using ProfileMeExample.Scenarios;

namespace ProfileMeExample.Pages;

/// <summary>
/// Base of every scenario screen: the title and the "How to profile this" toolbar item
/// come from the catalog, and <see cref="RunAsync"/> times a run and reports it.
/// </summary>
public class ScenarioPage : ContentPage
{
    private ScenarioInfo? _scenario;

    protected ScenarioInfo Scenario => _scenario ?? throw new InvalidOperationException("UseScenario was not called.");

    protected void UseScenario(string id)
    {
        _scenario = ScenarioCatalog.Get(id);
        Title = _scenario.Title;
        ToolbarItems.Add(new ToolbarItem("Guide", null, () => _ = ShowGuideAsync()));
    }

    protected Task ShowGuideAsync() => Navigation.PushModalAsync(new TutorialPage(Scenario));

    /// <summary>Runs <paramref name="work"/> with the button disabled and writes its result and the wall clock to <paramref name="output"/>.</summary>
    protected async Task RunAsync(Button button, Label output, Func<Task<string>> work)
    {
        button.IsEnabled = false;
        output.Text = "Running...";
        var clock = Stopwatch.StartNew();
        try
        {
            var result = await work();
            output.Text = $"{result}\nWall clock: {clock.ElapsedMilliseconds:N0} ms";
        }
        catch (Exception ex)
        {
            output.Text = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            button.IsEnabled = true;
        }
    }
}
