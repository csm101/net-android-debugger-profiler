using NetAndroidProfiler.Core.Devices;
using NetAndroidProfiler.Core.Gui;

namespace NetAndroidProfiler.Tests.Device;

/// <summary>
/// The GUI driven over its control channel. No device is involved - what these need is the
/// built GUI and a session on disk - but they are tagged Device because they need something
/// the fast pass cannot assume: NapGui.exe, which only a machine with RAD Studio produces.
/// </summary>
[Trait("Category", "Device")]
[Collection("device")]
public class GuiChannelTests
{
    /// <summary>A ready session with a real trace behind it, built once for the whole class.</summary>
    private static string SessionDatabase()
    {
        string root = Path.Combine(Path.GetTempPath(), "net-android-profiler-tests", "gui-channel");
        string directory = Path.Combine(root, "20260101-000000-gui.test-sampling");
        string database = Path.Combine(directory, "session.db");
        if (File.Exists(database)) return database;

        Fast.Recorded.PrepareSampledSession(directory);
        return database;
    }

    private static async Task<GuiChannel> OpenAsync(CancellationToken ct)
    {
        Skip.If(ToolLocator.FindGui() is null, "NapGui.exe is not built: run gui\\build-gui.cmd (needs RAD Studio).");
        return await GuiChannel.StartAsync(ct);
    }

    [SkippableFact]
    public async Task The_gui_opens_a_session_and_draws_its_panels_without_showing_a_window()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var gui = await OpenAsync(cts.Token);

        var opened = await gui.OpenAsync(SessionDatabase(), "report", cts.Token);
        Assert.Equal("report", opened.GetProperty("panel").GetString());
        Assert.False(opened.GetProperty("visible").GetBoolean());

        foreach (var panel in new[] { "report", "tree", "graph", "summary" })
        {
            var picture = await gui.CaptureAsync(panel, "panel", 1200, 800, savePath: null, wantBytes: true, trim: true, cts.Token);
            Assert.NotNull(picture.Png);
            // The eight bytes every PNG starts with: what came back is a picture, not a message.
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, picture.Png!.Take(8));
            Assert.True(picture.Width > 300 && picture.Height > 200, $"{panel} was drawn {picture.Width}x{picture.Height}");
            Assert.False(picture.IsBlank, $"the {panel} panel came back blank");
        }
    }

    [SkippableFact]
    public async Task A_method_can_be_focused_and_the_call_graph_follows_it()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var gui = await OpenAsync(cts.Token);
        await gui.OpenAsync(SessionDatabase(), "report", cts.Token);

        var viewed = await gui.ViewAsync("graph", "TestTarget", filter: null, sortBy: null, ascending: false, cts.Token);
        Assert.Contains("TestTarget", viewed.GetProperty("method").GetString());
        Assert.Equal("graph", viewed.GetProperty("panel").GetString());

        var picture = await gui.CaptureAsync(null, "panel", 1200, 800, savePath: null, wantBytes: true, trim: true, cts.Token);
        Assert.False(picture.IsBlank);
        Assert.Equal("graph", picture.Panel);
    }

    [SkippableFact]
    public async Task A_capture_can_be_saved_to_a_file_instead_of_travelling_inline()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var gui = await OpenAsync(cts.Token);
        await gui.OpenAsync(SessionDatabase(), "report", cts.Token);

        string file = Path.Combine(Path.GetTempPath(), "net-android-profiler-tests", "gui-channel", "report.png");
        var picture = await gui.CaptureAsync("report", "panel", 1000, 700, file, wantBytes: false, trim: true, cts.Token);
        Assert.Null(picture.Png);
        Assert.Equal(Path.GetFullPath(file), picture.SavedTo);
        Assert.True(new FileInfo(file).Length > 1000, "the saved picture is suspiciously small");
    }

    /// <summary>
    /// The call graph draws a few boxes on a large canvas: untrimmed it is mostly white, and
    /// a picture that is mostly white says nothing in a report.
    /// </summary>
    [SkippableFact]
    public async Task A_canvas_panel_comes_back_cut_to_its_drawing_unless_the_whole_panel_is_asked_for()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var gui = await OpenAsync(cts.Token);
        await gui.OpenAsync(SessionDatabase(), "report", cts.Token);
        await gui.ViewAsync("graph", "TestTarget", null, null, false, cts.Token);

        var whole = await gui.CaptureAsync(null, "panel", 1400, 900, null, wantBytes: false, trim: false, cts.Token);
        var trimmed = await gui.CaptureAsync(null, "panel", 1400, 900, null, wantBytes: false, trim: true, cts.Token);
        Assert.False(whole.Trimmed);
        Assert.True(trimmed.Trimmed, "the call graph was not trimmed");
        Assert.True(trimmed.Width < whole.Width && trimmed.Height < whole.Height,
            $"trimmed {trimmed.Width}x{trimmed.Height} against {whole.Width}x{whole.Height}");
        Assert.False(trimmed.IsBlank);
    }

    [SkippableFact]
    public async Task A_command_the_window_refuses_is_an_error_with_the_reason_in_it()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var gui = await OpenAsync(cts.Token);

        var missing = await Assert.ThrowsAsync<Core.Sessions.ProfilerException>(
            () => gui.OpenAsync(Path.Combine(Path.GetTempPath(), "no-such-session"), null, cts.Token));
        Assert.Contains("session database", missing.Message, StringComparison.OrdinalIgnoreCase);

        await gui.OpenAsync(SessionDatabase(), null, cts.Token);
        var wrongPanel = await Assert.ThrowsAsync<Core.Sessions.ProfilerException>(
            () => gui.ViewAsync("nonsense", null, null, null, false, cts.Token));
        Assert.Contains("report", wrongPanel.Message);
    }
}
