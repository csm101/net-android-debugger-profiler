using Android.Content;
using Android.OS;

namespace TestTarget;

/// <summary>
/// Runs in a separate Android process (":helper"), mirroring apps that spawn
/// helper processes at startup (e.g. a background crash-report sender).
/// Breakpoint fodder: <see cref="HelperTick"/> every second.
/// </summary>
[Service(Name = "net.androiddebugger.testtarget.HelperService", Process = ":helper", Exported = false)]
public class HelperService : Service
{
    private Timer? _timer;
    private long _ticks;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        _timer ??= new Timer(_ => HelperTick(), null, 1000, 1000);
        return StartCommandResult.Sticky;
    }

    private void HelperTick()
    {
        long now = System.Environment.TickCount64;
        _ticks++;
        string message = $"helper tick {_ticks} at {now}";
        Android.Util.Log.Debug("TestTarget", message);
    }
}
