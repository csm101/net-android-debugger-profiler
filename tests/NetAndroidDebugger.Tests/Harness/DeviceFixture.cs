using NetAndroidDebugger.Core;
using NetAndroidDebugger.Core.Launch;

namespace NetAndroidDebugger.Tests.Harness;

/// <summary>
/// Per-run fixture: resolves the device and deploys TestTarget once (unless NAD_SKIP_DEPLOY=1).
/// Every test then gets its own <see cref="DebugSession"/> via <see cref="NewSession"/>.
/// </summary>
public sealed class DeviceFixture : IAsyncLifetime
{
    public string Serial { get; private set; } = "";
    public List<string> Log { get; } = new();

    public async Task InitializeAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        Serial = await TestEnvironment.ResolveDeviceSerialAsync(cts.Token);
        if (!TestEnvironment.SkipDeploy)
        {
            var launcher = new AndroidLauncher(new AdbClient(), TestEnvironment.TestTargetApp(), new LaunchOptions(Serial), Log.Add);
            await launcher.DeployAsync(cts.Token);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public LaunchOptions Options(int basePort = 10000) => new(Serial, BaseSdbPort: basePort);

    public DebugSession NewSession(Action<string>? log = null) => new(line =>
    {
        lock (Log) Log.Add(line);
        // The engine logs from background threads and those can outlive the test that started the
        // session; xUnit's output helper throws once its test is over, and an unhandled throw on a
        // background thread takes the whole test host down. The line is already in Log.
        try { log?.Invoke(line); }
        catch (InvalidOperationException) { }
    });
}

[CollectionDefinition(Name)]
public sealed class DeviceCollection : ICollectionFixture<DeviceFixture>
{
    public const string Name = "device";
}
