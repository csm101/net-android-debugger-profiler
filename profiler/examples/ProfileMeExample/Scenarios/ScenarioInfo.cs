namespace ProfileMeExample.Scenarios;

/// <summary>
/// One screen of the app: the problem it carries, the profiler feature that exposes it and
/// the guide shown behind "How to profile this".
/// </summary>
/// <param name="Id">Stable key; also the Shell route of the page.</param>
/// <param name="Title">Screen title.</param>
/// <param name="Symptom">What the user of the app sees, one sentence.</param>
/// <param name="Feature">The profiler feature this screen is a demo of.</param>
/// <param name="PageType">The page to open.</param>
/// <param name="Guide">What to look for in the profiler, as shown in the app.</param>
/// <param name="ManualUrl">Manual page for this scenario, when the manual exists; the guide dialog offers it.</param>
public sealed record ScenarioInfo(
    string Id,
    string Title,
    string Symptom,
    string Feature,
    Type PageType,
    string Guide,
    Uri? ManualUrl = null)
{
    public string Route => Id;
}
