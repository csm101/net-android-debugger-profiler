using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NetAndroidProfiler.Core.Devices;
using NetAndroidProfiler.Core.Sessions;

namespace NetAndroidProfiler.Core.Gui;

/// <summary>
/// The desktop GUI, driven as a process: open a session in it, look at a panel, focus a
/// method, and be handed a picture of what is on it. The window is not shown unless
/// somebody asks for it, so a picture costs nothing on the user's screen and works when
/// they are not at it.
///
/// The GUI is started with <c>--control</c> and speaks one JSON object per line over its
/// standard input and output (its uGuiControl unit is the other half of this). It
/// announces itself with a <c>ready</c> line, the way dsrouter does, so that starting it
/// is a wait for a fact rather than a sleep.
///
/// One channel owns one window: dispose it and the window goes.
/// </summary>
public sealed class GuiChannel : IAsyncDisposable
{
    private readonly Process _process;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly List<string> _log = [];
    private int _nextId;

    private GuiChannel(Process process) => _process = process;

    /// <summary>Where the window came from, for a report that says what it is showing.</summary>
    public string ExecutablePath { get; private init; } = "";

    /// <summary>The last lines the GUI wrote to its error stream: what a failure has to say.</summary>
    public IReadOnlyList<string> Log
    {
        get { lock (_log) return _log.ToList(); }
    }

    /// <summary>True while the window is still there.</summary>
    public bool IsRunning => !_process.HasExited;

    /// <summary>
    /// Starts the GUI in control mode and waits for its ready line.
    /// </summary>
    /// <param name="guiPath">The executable; found through <see cref="ToolLocator.FindGui"/> when null.</param>
    public static async Task<GuiChannel> StartAsync(CancellationToken ct, string? guiPath = null)
    {
        string exe = guiPath ?? ToolLocator.FindGui()
            ?? throw new ToolException(
                "The profiler's GUI (NapGui.exe) was not found, so there is nothing to draw a picture with. " +
                "It ships in the package's gui folder next to bin; set NETANDROIDPROFILER_NAPGUI to it if it lives elsewhere.");

        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--control");
        // The window belongs to this process: a server that is killed rather than closed would
        // otherwise leave a hidden window behind, holding its files, for the rest of the day.
        psi.ArgumentList.Add($"--parent-pid={Environment.ProcessId}");

        var process = Process.Start(psi) ?? throw new ToolException($"Could not start {exe}.");
        var channel = new GuiChannel(process) { ExecutablePath = exe };
        process.ErrorDataReceived += (_, e) => channel.Remember(e.Data);
        process.BeginErrorReadLine();

        try
        {
            var ready = await channel.ReadLineAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(ready);
            if (!document.RootElement.TryGetProperty("ready", out var flag) || !flag.GetBoolean())
                throw new ToolException($"The GUI did not report itself ready: {ready}");
            return channel;
        }
        catch
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Open a session database, or the folder that holds one.</summary>
    public Task<JsonElement> OpenAsync(string path, string? panel, CancellationToken ct) =>
        SendAsync(new Dictionary<string, object?> { ["command"] = "open", ["path"] = path, ["panel"] = panel }, ct);

    /// <summary>Choose what is on screen: a panel, a method to focus, a filter, a sort.</summary>
    public Task<JsonElement> ViewAsync(string? panel, string? method, string? filter, string? sortBy, bool ascending, CancellationToken ct) =>
        SendAsync(new Dictionary<string, object?>
        {
            ["command"] = "view",
            ["panel"] = panel,
            ["method"] = method,
            ["filter"] = filter,
            ["sortBy"] = sortBy,
            ["ascending"] = ascending,
        }, ct);

    /// <summary>
    /// A picture of a panel, or of the whole window. The bytes are a PNG; when
    /// <paramref name="savePath"/> is given the GUI writes the file itself, which keeps a
    /// large picture out of the channel. With <paramref name="trim"/> the empty margins are
    /// cut off, which is what makes a canvas panel readable in a report.
    /// </summary>
    public async Task<GuiPicture> CaptureAsync(string? panel, string target, int width, int height,
        string? savePath, bool wantBytes, bool trim, CancellationToken ct)
    {
        var answer = await SendAsync(new Dictionary<string, object?>
        {
            ["command"] = "capture",
            ["panel"] = panel,
            ["target"] = target,
            ["width"] = width,
            ["height"] = height,
            ["path"] = savePath,
            ["inline"] = wantBytes,
            ["trim"] = trim,
        }, ct).ConfigureAwait(false);

        byte[]? png = null;
        if (answer.TryGetProperty("png", out var encoded) && encoded.ValueKind == JsonValueKind.String)
            png = Convert.FromBase64String(encoded.GetString()!);
        return new GuiPicture(
            png,
            answer.TryGetProperty("path", out var saved) ? saved.GetString() : null,
            answer.GetProperty("width").GetInt32(),
            answer.GetProperty("height").GetInt32(),
            answer.TryGetProperty("blank", out var blank) && blank.GetBoolean(),
            answer.TryGetProperty("panel", out var shown) ? shown.GetString() ?? "" : "",
            answer.TryGetProperty("bytes", out var size) ? size.GetInt32() : png?.Length ?? 0,
            answer.TryGetProperty("trimmed", out var trimmed) && trimmed.GetBoolean());
    }

    /// <summary>Put the window on the user's screen, or take it off again.</summary>
    public Task<JsonElement> SetVisibleAsync(bool visible, CancellationToken ct) =>
        SendAsync(new Dictionary<string, object?> { ["command"] = visible ? "show" : "hide" }, ct);

    /// <summary>What the window is showing.</summary>
    public Task<JsonElement> StatusAsync(CancellationToken ct) =>
        SendAsync(new Dictionary<string, object?> { ["command"] = "status" }, ct);

    /// <summary>
    /// One command, one answer. Commands are serialized: the window is one thing and the
    /// answers come back in the order they were asked for.
    /// </summary>
    public async Task<JsonElement> SendAsync(Dictionary<string, object?> command, CancellationToken ct)
    {
        if (_process.HasExited)
            throw new ProfilerException($"The GUI is no longer running. {LastWords()}");

        await _turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            command["id"] = ++_nextId;
            var request = JsonSerializer.Serialize(command, GuiJson.Options);
            await _process.StandardInput.WriteLineAsync(request.AsMemory(), ct).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);

            // A render of a large panel is the slow case, and it is still under a second;
            // a minute means the window is wedged, not busy.
            var line = await ReadLineAsync(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
            var answer = JsonDocument.Parse(line).RootElement.Clone();
            if (answer.TryGetProperty("ok", out var ok) && !ok.GetBoolean())
                throw new ProfilerException(answer.TryGetProperty("error", out var error)
                    ? error.GetString() ?? "The GUI refused the command."
                    : "The GUI refused the command.");
            return answer;
        }
        finally
        {
            _turn.Release();
        }
    }

    private async Task<string> ReadLineAsync(TimeSpan within, CancellationToken ct)
    {
        var read = _process.StandardOutput.ReadLineAsync(ct).AsTask();
        var finished = await Task.WhenAny(read, Task.Delay(within, ct)).ConfigureAwait(false);
        if (finished != read)
            throw new ProfilerException($"The GUI did not answer within {within.TotalSeconds:F0}s. {LastWords()}");
        return await read.ConfigureAwait(false)
            ?? throw new ProfilerException($"The GUI closed its side of the channel. {LastWords()}");
    }

    private string LastWords() =>
        Log.Count == 0 ? "It said nothing on the way out." : "Last words: " + string.Join(" | ", Log.TakeLast(5));

    private void Remember(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_log)
        {
            _log.Add(line);
            if (_log.Count > 200) _log.RemoveRange(0, _log.Count - 200);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Closing the input is how the window is asked to go: the channel's end of stream
        // is what the GUI waits for. Killing it is the answer to a window that will not.
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        catch { /* best effort */ }
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch { /* best effort */ }
        _process.Dispose();
        _turn.Dispose();
    }
}

/// <summary>A picture the GUI drew: the bytes, where they were saved, and how big it is.</summary>
/// <param name="Png">The PNG itself, when it was asked for inline.</param>
/// <param name="SavedTo">Where the GUI wrote it, when a path was given.</param>
/// <param name="Width">Pixels across.</param>
/// <param name="Height">Pixels down.</param>
/// <param name="IsBlank">True when the picture is one flat colour: a panel with nothing on it.</param>
/// <param name="Panel">The panel that was drawn.</param>
/// <param name="Bytes">The size of the PNG, whether or not it travelled inline.</param>
/// <param name="Trimmed">True when empty margins were cut off, so the size is the drawing's.</param>
public sealed record GuiPicture(byte[]? Png, string? SavedTo, int Width, int Height, bool IsBlank, string Panel, int Bytes, bool Trimmed);

internal static class GuiJson
{
    /// <summary>Nulls are left out: the GUI reads a missing member as "not asked for".</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
