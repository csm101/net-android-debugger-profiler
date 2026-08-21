using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NetAndroidProfiler.Cli;

namespace NetAndroidProfiler.Tests.Fast;

/// <summary>
/// The local control service the Delphi GUI drives (U7). These exercise the wire contract
/// without a device: what the service answers, and how it fails.
/// </summary>
public class ControlServiceTests : IAsyncLifetime
{
    private ControlService _service = null!;
    private HttpClient _client = null!;
    private string _root = null!;

    public Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "net-android-profiler-tests", "control", Guid.NewGuid().ToString("N"));
        _service = new ControlService(0, _root);
        _service.Start();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_service.Port}/") };
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _service.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Health_reports_the_version_and_where_sessions_live()
    {
        var response = await _client.GetAsync("health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("version").GetString()));
        Assert.Equal(_root, doc.RootElement.GetProperty("sessionsRoot").GetString());
        Assert.Equal(_service.Port, doc.RootElement.GetProperty("port").GetInt32());
    }

    [Fact]
    public async Task An_empty_sessions_root_lists_no_sessions()
    {
        var sessions = await _client.GetFromJsonAsync<List<SessionListItem>>("sessions");
        Assert.NotNull(sessions);
        Assert.Empty(sessions!);
    }

    [Fact]
    public async Task An_unknown_route_is_a_404_naming_what_was_asked()
    {
        var response = await _client.GetAsync("nope/at/all");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("nope/at/all", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_spec_the_engine_rejects_comes_back_as_400_with_the_guidance()
    {
        var response = await _client.PostAsync("sessions", Body("""
            { "deviceSerial": "emulator-5556", "packageName": "com.example.app", "mode": "nonsense" }
            """));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("sampling", body);          // the message lists the modes that do exist
    }

    [Fact]
    public async Task Instrumenting_without_a_callspec_is_refused_before_touching_a_device()
    {
        var response = await _client.PostAsync("sessions", Body("""
            { "deviceSerial": "emulator-5556", "packageName": "com.example.app", "mode": "instrumenting" }
            """));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("callspec", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Malformed_json_is_a_400_not_a_500()
    {
        var response = await _client.PostAsync("sessions", Body("{ this is not json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("JSON", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_session_this_service_did_not_start_cannot_be_controlled()
    {
        var response = await _client.PostAsync("sessions/20260101-000000-nope/stop", Body(""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not started by this service", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The session is registered before it runs, so it can be addressed even though it
    /// fails moments later against a device that is not there - which is exactly what
    /// makes the control verbs testable without hardware.
    /// </summary>
    [Fact]
    public async Task The_live_control_verbs_answer_501_until_the_engine_supports_them()
    {
        var created = await _client.PostAsync("sessions", Body("""
            { "deviceSerial": "no-such-device", "packageName": "com.example.app", "mode": "sampling", "durationSeconds": 1 }
            """));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        string id = doc.RootElement.GetProperty("id").GetString()!;

        foreach (string verb in new[] { "pause", "resume", "snapshot", "clear" })
        {
            var response = await _client.PostAsync($"sessions/{id}/{verb}", Body(""));
            Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
            string body = await response.Content.ReadAsStringAsync();
            Assert.Contains(verb, body);
            Assert.Contains("segments", body);      // says what is missing, not just "no"
        }

        // The session itself is addressable and reports its state.
        var status = await _client.GetAsync($"sessions/{id}");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Contains(id, await status.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Shutdown_signals_the_host_to_stop()
    {
        Assert.False(_service.Stopping.IsCancellationRequested);
        var response = await _client.PostAsync("shutdown", Body(""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(_service.Stopping.IsCancellationRequested);
    }

    /// <summary>
    /// Windows' HTTP stack answers 411 to a POST that carries neither Content-Length nor
    /// chunked encoding, before the service ever sees the request. Every real HTTP client
    /// sets the header (including for an empty body), but `curl -X POST` without data does
    /// not - which is exactly how someone testing the GUI's endpoints by hand meets it.
    /// Documented as a test so it is not rediscovered as a bug in the service.
    /// </summary>
    [Fact]
    public async Task A_post_without_a_content_length_is_rejected_by_the_http_stack()
    {
        using var socket = new System.Net.Sockets.TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, _service.Port);
        await using var stream = socket.GetStream();
        byte[] request = Encoding.ASCII.GetBytes("POST /sessions HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        string statusLine = (await reader.ReadLineAsync()) ?? "";
        Assert.Contains("411", statusLine);
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");
}
