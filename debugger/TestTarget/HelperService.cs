using Android.Content;
using Android.OS;

namespace TestTarget;

/// <summary>
/// Lives in the main process and kills it on request, so tests can watch how the debugger
/// reports an app that dies on its own:
/// <c>adb shell am broadcast -a net.androiddebugger.testtarget.KILL_APP</c>.
/// </summary>
[BroadcastReceiver(Name = "net.androiddebugger.testtarget.AppKillReceiver", Exported = true)]
[IntentFilter([AppKillReceiver.KillAction])]
public class AppKillReceiver : BroadcastReceiver
{
    public const string KillAction = "net.androiddebugger.testtarget.KILL_APP";

    public override void OnReceive(Context? context, Intent? intent)
    {
        Android.Util.Log.Debug("TestTarget", "main process killing itself on request");
        Android.OS.Process.KillProcess(Android.OS.Process.MyPid());
    }
}

/// <summary>
/// Lives in the ":helper" process and kills it on request, so tests can watch Android restart a
/// sticky service and the debugger re-attach the new process:
/// <c>adb shell am broadcast -a net.androiddebugger.testtarget.KILL_HELPER</c>.
/// </summary>
[BroadcastReceiver(Name = "net.androiddebugger.testtarget.HelperKillReceiver", Exported = true, Process = ":helper")]
[IntentFilter([HelperKillReceiver.KillAction])]
public class HelperKillReceiver : BroadcastReceiver
{
    public const string KillAction = "net.androiddebugger.testtarget.KILL_HELPER";

    public override void OnReceive(Context? context, Intent? intent)
    {
        Android.Util.Log.Debug("TestTarget", "helper process killing itself on request");
        Android.OS.Process.KillProcess(Android.OS.Process.MyPid());
    }
}

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

/// <summary>
/// Runs in a process whose name has nothing to do with the package, the way a component declared
/// with a global <c>android:process</c> does (the reference application ships one). Nothing in the process name says
/// it belongs to the app, so only its uid does — which is what the launcher has to rely on:
/// <c>adb shell am broadcast -a net.androiddebugger.testtarget.SPAWN_GLOBAL</c>.
/// </summary>
[BroadcastReceiver(Name = "net.androiddebugger.testtarget.GlobalProcessReceiver", Exported = true, Process = "net.androiddebugger.globalproc")]
[IntentFilter([GlobalProcessReceiver.SpawnAction])]
public class GlobalProcessReceiver : BroadcastReceiver
{
    public const string SpawnAction = "net.androiddebugger.testtarget.SPAWN_GLOBAL";

    public override void OnReceive(Context? context, Intent? intent)
    {
        int computed = Compute();
        Android.Util.Log.Debug("TestTarget", $"global-process receiver ran: {computed}");
    }

    private static int Compute()
    {
        int value = 456; // marker: global-process-compute
        Android.Util.Log.Verbose("TestTarget", $"global compute {value}");
        return value;
    }
}
