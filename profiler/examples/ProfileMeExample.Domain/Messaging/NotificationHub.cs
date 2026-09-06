namespace ProfileMeExample.Domain.Messaging;

public sealed record Notification(string Text, DateTime At);

/// <summary>Application-wide notifications, delivered through a static event.</summary>
public static class NotificationHub
{
    public static event Action<Notification>? Published;

    /// <summary>How many handlers are attached right now: one per widget that is still alive.</summary>
    public static int SubscriberCount => Published?.GetInvocationList().Length ?? 0;

    public static void Publish(string text) => Published?.Invoke(new Notification(text, DateTime.Now));
}

/// <summary>
/// A dashboard widget that subscribes to <see cref="NotificationHub"/> when created.
/// </summary>
/// <remarks>
/// Defect on purpose: it never unsubscribes. Closing the screen drops the UI's reference,
/// but the static event keeps the widget, and the quarter-megabyte cache it owns, alive
/// for the life of the process. Two heap snapshots around a few open/close cycles show
/// <c>DashboardWidget</c> and <c>byte[]</c> growing by exactly one and one per cycle.
/// </remarks>
public sealed class DashboardWidget
{
    private const int ThumbnailCacheBytes = 256 * 1024;

    private readonly byte[] _thumbnailCache = new byte[ThumbnailCacheBytes];

    public DashboardWidget(string name)
    {
        Name = name;
        _thumbnailCache[0] = 1;
        NotificationHub.Published += OnPublished;
    }

    public string Name { get; }

    public int Received { get; private set; }

    public int CacheBytes => _thumbnailCache.Length;

    private void OnPublished(Notification notification) => Received++;
}
