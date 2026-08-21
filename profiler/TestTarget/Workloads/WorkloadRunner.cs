namespace TestTarget.Workloads;

/// <summary>
/// Background loop alternating the CPU-heavy and allocation-heavy workloads so
/// that any profiling mode (sampling, gcdump, enter/leave) has something
/// recognizable to observe.
/// </summary>
public sealed class WorkloadRunner
{
    private readonly Thread _thread;
    private readonly CpuBurner _cpu = new();
    private readonly AllocHog _alloc = new();
    private readonly SequenceProducer _sequence = new();

    private WorkloadRunner()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "TestTargetWorkload" };
    }

    public static WorkloadRunner Start()
    {
        var r = new WorkloadRunner();
        r._thread.Start();
        // Second thread with the leaf-attribution probe (U15); light enough not to
        // disturb the main workload's share of the profile.
        LeafProbe.Start();
        return r;
    }

    private void Loop()
    {
        long iteration = 0;
        while (true)
        {
            iteration++;
            long cpuResult = _cpu.Busy(iteration);
            int allocCount = _alloc.Allocate(iteration);
            long sequenceSum = _sequence.Consume(SequenceProducer.ItemsPerIteration);
            Android.Util.Log.Info("TestTarget", $"iteration={iteration} cpu={cpuResult} alloc={allocCount} seq={sequenceSum}");
            Thread.Sleep(200);
        }
    }
}
