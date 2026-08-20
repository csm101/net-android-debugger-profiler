using Android.Content;
using Android.Widget;

namespace TestTarget;

/// <summary>
/// Minimal debuggee. Two breakpoint fodders:
/// - <see cref="OnIncrementClicked"/>: runs on the UI thread on button tap.
/// - <see cref="Tick"/>: runs every second on a background thread, no UI needed.
/// Extend freely whenever a new debugger feature needs a scenario.
/// </summary>
[Activity(Label = "@string/app_name", MainLauncher = true)]
public class MainActivity : Activity
{
    private int _counter;
    private long _ticks;
    private TextView? _label;
    private Timer? _timer;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_main);

        _label = FindViewById<TextView>(Resource.Id.counter_label);
        var button = FindViewById<Button>(Resource.Id.increment_button);
        button!.Click += OnIncrementClicked;

        _timer = new Timer(_ => Tick(), null, 1000, 1000);

        // Spawn the helper process right away, like the reference application does in Application init.
        StartService(new Intent(this, typeof(HelperService)));
    }

    private void OnIncrementClicked(object? sender, EventArgs e)
    {
        int previous = _counter;
        _counter = previous + 1;
        string text = $"Counter: {_counter}";
        _label!.Text = text;
        Android.Util.Log.Info("TestTarget", text);
    }

    private void Tick()
    {
        long now = Environment.TickCount64;
        _ticks++;
        string message = $"tick {_ticks} at {now}";
        // A null local (value formatting: no expansion handle) and debuggee traces
        // (must surface as app output, not debugger log).
        object? nothing = _ticks < 0 ? new object() : null;
        System.Diagnostics.Debug.WriteLine($"trace {message}");
        Console.WriteLine($"console {message}");
        Android.Util.Log.Debug("TestTarget", message);
    }
}
