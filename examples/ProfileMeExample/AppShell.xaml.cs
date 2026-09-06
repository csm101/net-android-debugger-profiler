using ProfileMeExample.Scenarios;

namespace ProfileMeExample;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        foreach (var scenario in ScenarioCatalog.All)
            Routing.RegisterRoute(scenario.Route, scenario.PageType);
    }
}
