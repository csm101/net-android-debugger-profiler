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
    private long _supportChecksum;

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
            // Work in a second assembly, so a session has more than one module to name.
            _supportChecksum = TestTarget.Support.SupportWork.Checksum(2000);
            Android.Util.Log.Info("TestTarget", $"iteration={iteration} cpu={cpuResult} alloc={allocCount} seq={sequenceSum} sup={_supportChecksum}");
            Thread.Sleep(200);
        }
    }
}
