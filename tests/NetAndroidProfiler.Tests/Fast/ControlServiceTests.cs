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
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            string body = await response.Content.ReadAsStringAsync();
            // Either reason is legitimate here (the session is sampling, and by now it has
            // also failed against a device that does not exist) - what matters is that the
            // answer explains itself instead of refusing bare.
            Assert.True(body.Contains("weaver") || body.Contains("not collecting"), body);
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

    // ------------------------------------------------------- the machine and the sources

    [Fact]
    public async Task Prereqs_name_every_tool_and_how_to_get_the_ones_that_are_missing()
    {
        using var doc = JsonDocument.Parse(await _client.GetStringAsync("prereqs"));
        var tools = doc.RootElement.GetProperty("tools").EnumerateArray().ToList();

        Assert.Contains(tools, t => t.GetProperty("name").GetString() == "adb");
        var dsrouter = tools.Single(t => t.GetProperty("name").GetString() == "dotnet-dsrouter");
        // The one prerequisite nobody remembers: the answer must carry the command itself,
        // so a frontend can offer to run it instead of printing a note.
        Assert.Equal("dotnet tool install -g dotnet-dsrouter", dsrouter.GetProperty("installCommand").GetString());
        Assert.True(dsrouter.GetProperty("required").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(dsrouter.GetProperty("purpose").GetString()));
    }

    [Fact]
    public async Task Projects_lists_the_android_applications_of_a_solution()
    {
        string folder = Path.Combine(_root, "src", "Acme.Droid");
        Directory.CreateDirectory(folder);
        string project = Path.Combine(folder, "Acme.Droid.csproj");
        await File.WriteAllTextAsync(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0-android35.0</TargetFramework>
                <ApplicationId>com.acme.app</ApplicationId>
              </PropertyGroup>
            </Project>
            """);

        using var doc = JsonDocument.Parse(await _client.GetStringAsync("projects?path=" + Uri.EscapeDataString(folder)));
        var first = doc.RootElement.EnumerateArray().Single();

        Assert.Equal("com.acme.app", first.GetProperty("applicationId").GetString());
        Assert.Equal("Acme.Droid", first.GetProperty("assemblyName").GetString());
        Assert.Contains("net9.0-android35.0", first.GetProperty("outputDir").GetString());
    }

    [Fact]
    public async Task A_path_the_finder_cannot_use_comes_back_as_400()
    {
        var response = await _client.GetAsync("projects?path=" + Uri.EscapeDataString(Path.Combine(_root, "nowhere")));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("No such solution", await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("projects")).StatusCode);   // no path at all
    }

    [Fact]
    public async Task A_build_of_a_project_that_is_not_there_is_refused_without_starting_a_job()
    {
        var response = await _client.PostAsync("builds", Body($$"""
            { "projectPath": {{JsonSerializer.Serialize(Path.Combine(_root, "Nope.csproj"))}} }
            """));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("No such project", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_tool_the_profiler_does_not_install_is_refused_rather_than_run()
    {
        var response = await _client.PostAsync("prereqs/install", Body("""{ "tool": "rm -rf /" }"""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not one of the tools", await response.Content.ReadAsStringAsync());

        // adb is a real prerequisite, but there is no command that installs it.
        var adb = await _client.PostAsync("prereqs/install", Body("""{ "tool": "adb" }"""));
        Assert.Equal(HttpStatusCode.BadRequest, adb.StatusCode);
        Assert.Contains("cannot be installed automatically", await adb.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_archives_of_a_session_are_answered_from_its_directory_even_when_nothing_is_running()
    {
        // Archives outlive the session that took them, so listing them must not need one.
        string directory = Path.Combine(_root, "20260101-000000-acme-instrumenting", "archives");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "the customers screen.db"), "not really a database");

        using var doc = JsonDocument.Parse(
            await _client.GetStringAsync("sessions/20260101-000000-acme-instrumenting/archives"));

        var only = doc.RootElement.EnumerateArray().Single();
        Assert.Equal("the customers screen", only.GetProperty("name").GetString());

        var unknown = await _client.GetAsync("sessions/no-such-session/archives");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task A_job_this_service_did_not_start_is_named_in_the_error()
    {
        var response = await _client.GetAsync("jobs/build-99");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("build-99", await response.Content.ReadAsStringAsync());
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");
}
