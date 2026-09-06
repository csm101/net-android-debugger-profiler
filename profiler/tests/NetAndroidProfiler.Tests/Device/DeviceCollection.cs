namespace NetAndroidProfiler.Tests.Device;

/// <summary>
/// Device tests share one emulator, one adb and one dsrouter port, and a session
/// force-stops and relaunches the app it profiles. xUnit runs distinct test classes
/// in parallel by default, which lets one class connect to the app another class is
/// profiling: the symptom is a session failing with "a different .NET process
/// connected to the profiler port" or with a dsrouter that never sees a runtime.
/// Every device class belongs to this collection so they run one at a time.
/// </summary>
[CollectionDefinition("device", DisableParallelization = true)]
public sealed class DeviceCollection { }
