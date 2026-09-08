using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace NetAndroidProfiler.Mcp;

/// <summary>
/// The desktop GUI as a way of showing results rather than describing them: a session is
/// opened in it, a panel is chosen, and the picture comes back in the answer. The window
/// stays off the screen unless <c>gui_show</c> is called, so this works when the person
/// asking is not sitting at that machine.
///
/// The window itself belongs to <see cref="GuiHost"/>: a tool class is disposed after the
/// call it served, and a window that lived here would close between one tool and the next.
/// </summary>
[McpServerToolType]
public sealed class GuiTools(SessionHost host, GuiHost gui)
{
    [McpServerTool(Name = "gui_open"), Description(
        "Opens a session in the profiler's GUI so that its panels can be drawn: the report, the call tree, " +
        "the call graph, the annotated source, the memory charts. The window is NOT shown - this is how a " +
        "picture is produced for an answer or a report; gui_show puts it on the user's screen when they ask. " +
        "sessionId takes what every other tool takes (an id, an archive path, or nothing for the last session).")]
    public async Task<string> GuiOpen(
        [Description("Session id, or the path of a result database (default: the last session)")] string? sessionId = null,
        [Description("Panel to show: report | tree | graph | source | memory | monitor | summary | explorer | log")] string? panel = null,
        [Description("Also put the window on the user's screen (default false: the picture is what comes back)")] bool visible = false,
        CancellationToken ct = default)
    {
        var database = DatabaseOf(sessionId, out var id);
        var window = await gui.ChannelAsync(ct);
        var answer = await window.OpenAsync(database, panel, ct);
        if (visible)
            await window.SetVisibleAsync(true, ct);
        return $"session {id} is open in the GUI ({window.ExecutablePath}), panel={Text(answer, "panel")}, " +
               $"window {(visible ? "on screen" : "not shown")}. gui_view chooses what to look at, gui_capture draws it.";
    }

    [McpServerTool(Name = "gui_view"), Description(
        "Chooses what the GUI is looking at before a capture: the panel, the method to focus (the Details, " +
        "Call graph and Source panels follow it), a filter on the report's method names, a column to sort by. " +
        "The method is matched exactly, then by prefix, then by any name containing it.")]
    public async Task<string> GuiView(
        [Description("Panel: report | tree | graph | source | memory | monitor | summary | explorer | log")] string? panel = null,
        [Description("Method to focus, e.g. My.App.Services.Sync or just Sync")] string? method = null,
        [Description("Show only methods whose name contains this")] string? filter = null,
        [Description("Column to sort the report by, e.g. self_samples, total_ns, calls")] string? sortBy = null,
        [Description("Sort ascending instead of descending")] bool ascending = false,
        CancellationToken ct = default)
    {
        var answer = await gui.Existing().ViewAsync(panel, method, filter, sortBy, ascending, ct);
        return $"panel={Text(answer, "panel")} method={Text(answer, "method")}";
    }

    [McpServerTool(Name = "gui_capture", ReadOnly = true), Description(
        "Draws what the GUI is showing and returns it as a PNG: a panel by default, the whole window with " +
        "target=window. The window is never brought to the screen for this, and nothing of the user's desktop " +
        "can end up in the picture - the GUI paints itself. Use it to show a call graph, a heap growth or a " +
        "hot method's source instead of describing it, and to put pictures in a report.")]
    public async Task<CallToolResult> GuiCapture(
        [Description("Panel to draw (default: the one being shown)")] string? panel = null,
        [Description("panel (default) or window")] string target = "panel",
        [Description("Window width in pixels (default 1400)")] int width = 1400,
        [Description("Window height in pixels (default 900)")] int height = 900,
        [Description("Host path to save the PNG to (folders are created)")] string? savePath = null,
        [Description("Return the image inline (default true). Set false with savePath to only save it.")] bool inline = true,
        CancellationToken ct = default)
    {
        var picture = await gui.Existing().CaptureAsync(panel, target, width, height, savePath, inline, ct);

        var text = new StringBuilder($"{picture.Panel} panel: {picture.Width}x{picture.Height} px, {picture.Bytes} bytes");
        if (picture.SavedTo is not null) text.Append(", saved to ").Append(picture.SavedTo);
        if (picture.IsBlank) text.Append(" - the panel is empty: this session has nothing for it");
        var result = new CallToolResult { Content = [new TextContentBlock { Text = text.ToString() }] };
        if (picture.Png is { Length: > 0 })
            result.Content.Add(ImageContentBlock.FromBytes(picture.Png, "image/png"));
        return result;
    }

    [McpServerTool(Name = "gui_show"), Description(
        "Puts the GUI's window on the user's screen, on the session it already has open. Only when they ask " +
        "for it and are at that machine: over a remote session a captured picture is what reaches them.")]
    public async Task<string> GuiShow(CancellationToken ct = default)
    {
        var answer = await gui.Existing().SetVisibleAsync(true, ct);
        return $"the GUI is on screen, showing {Text(answer, "panel")} of {Text(answer, "session")}.";
    }

    [McpServerTool(Name = "gui_close"), Description("Closes the GUI window this server opened. Sessions and their databases are untouched.")]
    public async Task<string> GuiClose() =>
        await gui.CloseAsync() ? "the GUI window is closed." : "no GUI window is open.";

    /// <summary>
    /// The database behind a session id, resolved the way every read-only tool resolves it:
    /// an explicit id, an archive path ending in .db, or the last session.
    /// </summary>
    private string DatabaseOf(string? sessionId, out string id)
    {
        if (sessionId is not null && sessionId.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(sessionId)) throw new McpException($"No result database at {sessionId}.");
            id = Path.GetFileNameWithoutExtension(sessionId);
            return Path.GetFullPath(sessionId);
        }
        id = host.ResolveId(sessionId);
        var database = Path.Combine(host.SessionsRoot, id, "session.db");
        if (!File.Exists(database))
            throw new McpException($"Session {id} has no result database yet ({database}).");
        return database;
    }

    private static string Text(JsonElement answer, string name) =>
        answer.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
}
