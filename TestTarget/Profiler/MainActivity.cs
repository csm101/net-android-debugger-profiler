using TestTarget.Workloads;

namespace TestTarget;

[Activity(Label = "@string/app_name", MainLauncher = true)]
public class MainActivity : Activity
{
    private static WorkloadRunner? _runner;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_main);

        // Start the synthetic workloads once per process; they run until the
        // process dies so any profiling session sees a steady stream of work.
        _runner ??= WorkloadRunner.Start();
    }
}
