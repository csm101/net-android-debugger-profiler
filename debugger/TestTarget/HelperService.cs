using Android.Content;
using Android.OS;

namespace TestTarget;

/// <summary>
/// Runs in a third process (":late"), started by Android when the broadcast arrives — not by
/// the app's main process. That is what makes it usable to test a process appearing *while the
/// main process is suspended at a breakpoint*:
/// <c>adb shell am broadcast -a net.androiddebugger.testtarget.SPAWN_LATE</c>.
/// </summary>
[BroadcastReceiver(Name = "net.androiddebugger.testtarget.LateReceiver", Exported = true, Process = ":late")]
[IntentFilter([LateReceiver.SpawnAction])]
public class LateReceiver : BroadcastReceiver
{
    public const string SpawnAction = "net.androiddebugger.testtarget.SPAWN_LATE";

    public override void OnReceive(Context? context, Intent? intent)
    {
        int computed = Compute();
        Android.Util.Log.Debug("TestTarget", $"late receiver ran: {computed}");
    }

    private static int Compute()
    {
        int value = 123;
        Android.Util.Log.Verbose("TestTarget", $"late compute {value}");
        return value;
    }
}

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
