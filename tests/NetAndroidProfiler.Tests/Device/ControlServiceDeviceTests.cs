using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NetAndroidProfiler.Cli;

namespace NetAndroidProfiler.Tests.Device;

/// <summary>
/// One session driven entirely over HTTP, which is how the Delphi GUI drives the engine:
/// POST /sessions, poll /sessions/{id} and /counters while it collects, POST stop, then
/// read the database the service reports. The fast control-service tests cover the wire
/// contract without a device; this covers the part that needs one.
/// </summary>
[Trait("Category", "Device")]
[Collection("device")]
public sealed class ControlServiceDeviceTests : IAsyncLifetime
{
    private static string Serial => Environment.GetEnvironmentVariable("NAP_TEST_SERIAL") ?? "emulator-5556";
    private static string Package => Environment.GetEnvironmentVariable("NAP_TEST_PACKAGE") ?? "com.mcasoftware.testtarget";

    private ControlService _service = null!;
    private HttpClient _client = null!;
    private string _root = null!;

    public Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "net-android-profiler-tests", "control-device", Guid.NewGuid().ToString("N"));
        _service = new ControlService(0, _root);
        _service.Start();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_service.Port}/"), Timeout = TimeSpan.FromMinutes(5) };
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _service.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task A_session_runs_from_start_to_database_over_http()
    {
        // The device has to be there for the service to see it, and this is also the
        // check a frontend makes before offering to profile anything.
        var devices = JsonDocument.Parse(await _client.GetStringAsync("devices"));
        Assert.Contains(devices.RootElement.EnumerateArray(), d => d.GetProperty("serial").GetString() == Serial);

        var start = await _client.PostAsJsonAsync("sessions", new
        {
            deviceSerial = Serial,
            packageName = Package,
            mode = "sampling",
            durationSeconds = 12,
        });
        Assert.Equal(HttpStatusCode.Created, start.StatusCode);
        using var started = JsonDocument.Parse(await start.Content.ReadAsStringAsync());
        string id = started.RootElement.GetProperty("id").GetString()!;

        // While it collects, the counters have to move: that is what the Monitor panel draws.
        string state = await WaitForStateAsync(id, "Collecting", TimeSpan.FromMinutes(2));
        Assert.Equal("Collecting", state);
        using var counters = JsonDocument.Parse(await _client.GetStringAsync($"sessions/{id}/counters"));
        Assert.True(counters.RootElement.GetProperty("elapsedSeconds").GetDouble() >= 0);

        // Let it actually sample before stopping: stop is meant to end a session early, and
        // a session ended in its first second has nothing to show for itself.
        await Task.Delay(TimeSpan.FromSeconds(6));

        var stop = await _client.PostAsync($"sessions/{id}/stop", null);
        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);

        state = await WaitForStateAsync(id, "Ready", TimeSpan.FromMinutes(4));
        Assert.Equal("Ready", state);

        using var final = JsonDocument.Parse(await _client.GetStringAsync($"sessions/{id}"));
        string database = final.RootElement.GetProperty("databasePath").GetString()!;
        Assert.True(File.Exists(database), "the service reported a database that is not there: " + database);

        // The results are readable, and they are this session's: the GUI opens exactly this file.
        using var store = NetAndroidProfiler.Core.Store.ResultStore.Open(database);
        var session = store.ReadSession();
        Assert.NotNull(session);
        Assert.Equal(Package, session!.Package);
        Assert.True(session.TotalSamples > 0, "a sampling session must carry samples");

        // And the service lists it among the sessions it knows.
        using var list = JsonDocument.Parse(await _client.GetStringAsync("sessions"));
        Assert.Contains(list.RootElement.EnumerateArray(), s => s.GetProperty("id").GetString() == id);
    }

    private async Task<string> WaitForStateAsync(string id, string wanted, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        string state = "";
        while (DateTime.UtcNow < deadline)
        {
            using var doc = JsonDocument.Parse(await _client.GetStringAsync($"sessions/{id}"));
            state = doc.RootElement.GetProperty("state").GetString() ?? "";
            if (state == wanted) return state;
            if (state == "Failed")
                throw new Xunit.Sdk.XunitException($"session failed: {doc.RootElement.GetProperty("error").GetString()}");
            await Task.Delay(500);
        }
        return state;
    }
}
