using Android.Content;
using Android.Widget;

namespace TestTarget;

/// <summary>
/// Minimal debuggee. Breakpoint fodders:
/// - <see cref="OnIncrementClicked"/>: runs on the UI thread on button tap.
/// - <see cref="Tick"/>: runs every second on a background thread, no UI needed; exercises
///   locals of many shapes, a first-chance exception every fifth tick, and a call to
///   <see cref="Describe"/> for step into / step out.
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
        var sample = new Sample(_ticks);
        string described = Describe(sample);
        EvaluationProbe();
        _ = AsyncProbeAsync();
        // Test hook: `adb shell run-as <pkg> touch files/crash-on-tick` makes the next tick
        // throw an unhandled exception on this timer thread (kills the process).
        if (File.Exists(Path.Combine(FilesDir!.AbsolutePath, "crash-on-tick")))
        {
            throw new ApplicationException($"unhandled failure requested at tick {_ticks}");
        }
        if (_ticks % 5 == 0)
        {
            try
            {
                throw new InvalidOperationException($"expected failure at tick {_ticks}");
            }
            catch (InvalidOperationException ex)
            {
                Android.Util.Log.Debug("TestTarget", "caught: " + ex.Message);
            }
        }
        System.Diagnostics.Debug.WriteLine($"trace {message}");
        Console.WriteLine($"console {message}");
        Android.Util.Log.Debug("TestTarget", message);
    }

    /// <summary>
    /// Own frame for the slow-getter scenario: a <see cref="SlowProbe"/> local must never sit in
    /// <see cref="Tick"/>, or every test that reads Tick's locals pays for (and aborts) an
    /// 8-second invocation.
    /// </summary>
    /// <summary>
    /// Async fodder: a method with a real await, so stepping across an await point and
    /// inspecting an async frame can be tested (the reference application is async throughout).
    /// </summary>
    private async Task<int> AsyncProbeAsync()
    {
        int before = (int)(_ticks % 100);
        await Task.Delay(30).ConfigureAwait(false);
        int after = before + 1;
        Android.Util.Log.Verbose("TestTarget", $"async probe {before}->{after}");
        return after;
    }

    private void EvaluationProbe()
    {
        var slow = new SlowProbe();
        int fast = slow.FastValue;
        Android.Util.Log.Verbose("TestTarget", $"probe {fast}");
    }

    private static string Describe(Sample sample)
    {
        int count = sample.Numbers.Count;
        string text = $"{sample.Name}:{sample.Kind}:{count}";
        return text;
    }
}

/// <summary>
/// Evaluation-robustness fodder: a property whose getter is slower than any sane
/// evaluation timeout, next to a fast one. Used to study what an *aborted* debuggee
/// invocation does to the stopped thread (KNOWN_UNKNOWNS U11). Kept out of
/// <see cref="Sample"/> so expanding `sample` never triggers the slow getter.
/// </summary>
public sealed class SlowProbe
{
    public int FastValue => 7;

    public int SlowValue
    {
        get
        {
            Thread.Sleep(8000);
            return 42;
        }
    }
}

public enum SampleKind { None, Odd, Even }

/// <summary>Value-formatting fodder: primitives, enum, string, list, array, nested object, property.</summary>
public sealed class Sample
{
    public Sample(long tick)
    {
        Tick = tick;
        Name = $"sample-{tick}";
        Kind = tick % 2 == 0 ? SampleKind.Even : SampleKind.Odd;
        Numbers = new List<int> { 1, 2, 3 };
        Words = new[] { "alpha", "beta" };
        Map = new Dictionary<string, int> { ["one"] = 1, ["two"] = 2 };
        Ratio = 0.5;
        When = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        Inner = tick > 0 ? new Sample(0) { Inner = null } : null;
    }

    public long Tick { get; }
    public string Name { get; }
    public SampleKind Kind { get; }
    public List<int> Numbers { get; }
    public string[] Words { get; }
    public Dictionary<string, int> Map { get; }
    public double Ratio { get; }
    public DateTime When { get; }
    public Sample? Inner { get; set; }
    public int NumbersCount => Numbers.Count;

    /// <summary>
    /// A lazy sequence: its value is a compiler-generated iterator, so a debugger shows the state
    /// machine's fields and the elements only through the enumerator group.
    /// </summary>
    public IEnumerable<int> Sequence => Steps();

    private static IEnumerable<int> Steps()
    {
        yield return 1;
        yield return 2;
        yield return 3;
    }
}
