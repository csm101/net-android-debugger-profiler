using ModelContextProtocol;
using NetAndroidProfiler.Core.Gui;
using NetAndroidProfiler.Core.Sessions;

namespace NetAndroidProfiler.Mcp;

/// <summary>
/// Owns the one GUI window this server drives, for as long as the server runs. It is a
/// service of its own rather than a field of <see cref="GuiTools"/> because the tool
/// classes are disposed after a call: a window that lived there would be closed between
/// <c>gui_open</c> and the capture that follows it.
/// </summary>
public sealed class GuiHost : IAsyncDisposable
{
    private readonly SemaphoreSlim _turn = new(1, 1);
    private GuiChannel? _gui;

    /// <summary>The window, started if there is none. One window per server.</summary>
    public async Task<GuiChannel> ChannelAsync(CancellationToken ct)
    {
        await _turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_gui is { IsRunning: true }) return _gui;
            if (_gui is not null) await _gui.DisposeAsync().ConfigureAwait(false);
            try { _gui = await GuiChannel.StartAsync(ct).ConfigureAwait(false); }
            catch (ToolException e) { throw new McpException(e.Message); }
            return _gui;
        }
        finally { _turn.Release(); }
    }

    /// <summary>The window that is already open, or an error saying to open one.</summary>
    public GuiChannel Existing() =>
        _gui is { IsRunning: true } gui
            ? gui
            : throw new McpException("No GUI window is open: call gui_open with the session to look at first.");

    /// <summary>Closes the window, if there is one. Answers whether there was.</summary>
    public async Task<bool> CloseAsync()
    {
        await _turn.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_gui is null) return false;
            await _gui.DisposeAsync().ConfigureAwait(false);
            _gui = null;
            return true;
        }
        finally { _turn.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_gui is not null) await _gui.DisposeAsync().ConfigureAwait(false);
        _gui = null;
        _turn.Dispose();
    }
}
