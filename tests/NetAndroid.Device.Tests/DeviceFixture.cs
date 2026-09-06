namespace NetAndroid.Device.Tests;

/// <summary>
/// The device the adb-level tests run against. Named by <c>NAD_DEVICE_SERIAL</c> or
/// <c>NAP_TEST_SERIAL</c> (the two products' variables, either will do), else the only device
/// online; with several attached and none named the fixture refuses, as both products do.
/// </summary>
public sealed class DeviceFixture : IAsyncLifetime
{
    public string Serial { get; private set; } = "";

    public AdbClient Adb { get; } = new();

    public async Task InitializeAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var requested = Environment.GetEnvironmentVariable("NAD_DEVICE_SERIAL") ?? Environment.GetEnvironmentVariable("NAP_TEST_SERIAL");
        var online = (await Adb.ListDevicesAsync(cts.Token)).Where(d => d.State == "device").ToList();
        if (!string.IsNullOrEmpty(requested))
        {
            if (online.All(d => d.Serial != requested))
                throw new InvalidOperationException($"{requested} is not online. Online: {string.Join(", ", online.Select(d => d.Serial))}");
            Serial = requested;
            return;
        }
        if (online.Count == 1) { Serial = online[0].Serial; return; }
        throw new InvalidOperationException(
            $"{online.Count} devices online ({string.Join(", ", online.Select(d => d.Serial))}); set NAD_DEVICE_SERIAL or NAP_TEST_SERIAL to choose one.");
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DeviceCollection : ICollectionFixture<DeviceFixture>
{
    public const string Name = "device";
}
