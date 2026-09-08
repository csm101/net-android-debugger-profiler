using System.Text.Json;
using NetAndroidProfiler.Core.Devices;
using NetAndroidProfiler.Tests.Support;

namespace NetAndroidProfiler.Tests.Device;

/// <summary>
/// The GUI tools through the MCP server, as an agent calls them: open a session, look at a
/// method, get a picture back. No device - what these need is the built GUI - but tagged
/// Device for the same reason as <see cref="GuiChannelTests"/>.
/// </summary>
[Trait("Category", "Device")]
[Collection("device")]
public sealed class GuiMcpTests : IDisposable
{
    private static string SessionsRoot => Path.Combine(Path.GetTempPath(), "net-android-profiler-tests", "gui-mcp");
    private const string SessionId = "20260101-000000-gui.mcp-sampling";

    private readonly McpServerFixture _server;

    public GuiMcpTests()
    {
        var directory = Path.Combine(SessionsRoot, SessionId);
        if (!File.Exists(Path.Combine(directory, "session.db")))
            Fast.Recorded.PrepareSampledSession(directory);
        _server = new McpServerFixture(SessionsRoot) { Timeout = TimeSpan.FromMinutes(3) };
        _server.Initialize();
    }

    public void Dispose() => _server.Dispose();

    [SkippableFact]
    public void Gui_open_view_and_capture_answer_with_a_picture_of_the_panel()
    {
        Skip.If(ToolLocator.FindGui() is null, "NapGui.exe is not built: run gui\\build-gui.cmd (needs RAD Studio).");
        try
        {
            var opened = _server.CallTool("gui_open", new { sessionId = SessionId, panel = "report" });
            Assert.Contains(SessionId, opened);
            Assert.Contains("not shown", opened);

            var viewed = _server.CallTool("gui_view", new { panel = "graph", method = "TestTarget" });
            Assert.Contains("panel=graph", viewed);

            var captured = _server.CallToolRaw("gui_capture", new { width = 1200, height = 800 });
            Assert.False(McpServerFixture.IsError(captured), captured.ToString());
            var content = captured.GetProperty("result").GetProperty("content");
            Assert.Equal(2, content.GetArrayLength());
            Assert.Equal("text", content[0].GetProperty("type").GetString());

            var image = content[1];
            Assert.Equal("image", image.GetProperty("type").GetString());
            Assert.Equal("image/png", image.GetProperty("mimeType").GetString());
            var png = Convert.FromBase64String(image.GetProperty("data").GetString()!);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4));
        }
        finally
        {
            _server.CallTool("gui_close", new { });
        }
    }

    [SkippableFact]
    public void A_capture_without_an_open_window_says_to_open_one()
    {
        Skip.If(ToolLocator.FindGui() is null, "NapGui.exe is not built: run gui\\build-gui.cmd (needs RAD Studio).");
        var answer = _server.CallToolRaw("gui_capture", new { });
        Assert.True(McpServerFixture.IsError(answer), answer.ToString());
        Assert.Contains("gui_open", answer.ToString());
    }
}
