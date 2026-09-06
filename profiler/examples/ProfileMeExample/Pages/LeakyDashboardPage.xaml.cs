using ProfileMeExample.Domain.Messaging;
using ProfileMeExample.Scenarios;

namespace ProfileMeExample.Pages;

public partial class LeakyDashboardPage : ScenarioPage
{
    private int _cycles;
    private int _published;

    public LeakyDashboardPage()
    {
        InitializeComponent();
        UseScenario(ScenarioCatalog.LeakyDashboard);
    }

    private void OnCycleClicked(object? sender, EventArgs e)
    {
        // Opening: the widget is created and shown. Closing: the screen drops its only
        // reference. The static event still holds it, and that is the leak.
        var widget = new DashboardWidget($"Dashboard #{++_cycles}");
        var cacheBytes = widget.CacheBytes;
        widget = null;
        Report($"Opened and closed dashboard #{_cycles} (cache of {cacheBytes / 1024} KB).");
    }

    private void OnPublishClicked(object? sender, EventArgs e)
    {
        NotificationHub.Publish($"Notification #{++_published}");
        Report($"Notification #{_published} delivered to {NotificationHub.SubscriberCount} widgets. One is on screen.");
    }

    private void OnCollectClicked(object? sender, EventArgs e)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Report("Full collection done.");
    }

    private void Report(string line) =>
        OutputLabel.Text = $"{line}\nWidgets still alive: {NotificationHub.SubscriberCount}, holding {NotificationHub.SubscriberCount * 256} KB of caches.";

    private async void OnGuideClicked(object? sender, EventArgs e) => await ShowGuideAsync();
}
