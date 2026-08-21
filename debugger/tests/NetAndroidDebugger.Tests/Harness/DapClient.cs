using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace NetAndroidDebugger.Tests.Harness;

/// <summary>
/// A minimal Debug Adapter Protocol client: starts the adapter process and speaks the wire format
/// to it. Deliberately not a library — the point of the end-to-end tests is to exercise the real
/// framing and the real process, the way an editor would.
/// </summary>
public sealed class DapClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly List<JsonObject> _events = new();
    private int _seq;

    private DapClient(Process process, Action<string> log)
    {
        _process = process;
        _stdin = process.StandardInput.BaseStream;
        _stdout = process.StandardOutput.BaseStream;
        _log = log;
    }

    public static DapClient Start(string adapterDll, Action<string> log)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(adapterDll);
        var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start the DAP adapter");
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log("[adapter stderr] " + e.Data); };
        process.BeginErrorReadLine();
        return new DapClient(process, log);
    }

    /// <summary>Events received while waiting for responses, oldest first.</summary>
    public IReadOnlyList<JsonObject> Events { get { lock (_events) return _events.ToList(); } }

    /// <summary>Sends a request and returns its response, collecting any events that arrive first.</summary>
    public async Task<JsonObject> SendAsync(string command, JsonObject? arguments, CancellationToken ct)
    {
        var seq = Interlocked.Increment(ref _seq);
        var request = new JsonObject { ["seq"] = seq, ["type"] = "request", ["command"] = command };
        if (arguments is not null) request["arguments"] = arguments;
        await WriteAsync(request, ct);

        while (true)
        {
            var message = await ReadAsync(ct) ?? throw new InvalidOperationException($"the adapter closed while waiting for '{command}'");
            var type = message["type"]?.GetValue<string>();
            if (type == "event")
            {
                lock (_events) _events.Add(message);
                _log($"[event] {message["event"]?.GetValue<string>()}");
                continue;
            }
            if (type == "response" && message["request_seq"]?.GetValue<int>() == seq)
            {
                var ok = message["success"]?.GetValue<bool>() ?? false;
                _log($"[{command}] {(ok ? "ok" : "ERROR " + message["message"]?.GetValue<string>())}");
                return message;
            }
        }
    }

    /// <summary>Sends a request and fails the test if the adapter answers with an error.</summary>
    public async Task<JsonObject> ExpectAsync(string command, JsonObject? arguments, CancellationToken ct)
    {
        var response = await SendAsync(command, arguments, ct);
        Assert.True(response["success"]?.GetValue<bool>() ?? false,
            $"{command} failed: {response["message"]?.GetValue<string>()}");
        return response["body"] as JsonObject ?? new JsonObject();
    }

    /// <summary>Waits for an event of the given name, consuming responses is not expected here.</summary>
    public async Task<JsonObject?> WaitForEventAsync(string name, TimeSpan timeout, CancellationToken ct)
    {
        lock (_events)
        {
            var already = _events.FirstOrDefault(e => e["event"]?.GetValue<string>() == name);
            if (already is not null) { _events.Remove(already); return already; }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var message = await ReadAsync(cts.Token);
                if (message is null) return null;
                if (message["type"]?.GetValue<string>() != "event")
                    continue;
                _log($"[event] {message["event"]?.GetValue<string>()}");
                if (message["event"]?.GetValue<string>() == name) return message;
                lock (_events) _events.Add(message);
            }
        }
        catch (OperationCanceledException) { }
        return null;
    }

    /// <summary>Writes bytes straight to the adapter, framing included — for malformed input.</summary>
    public async Task SendRawAsync(string raw, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(raw);
        await _stdin.WriteAsync(bytes, ct);
        await _stdin.FlushAsync(ct);
    }

    private async Task WriteAsync(JsonObject message, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(message.ToJsonString());
        var header = Encoding.UTF8.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        await _stdin.WriteAsync(header, ct);
        await _stdin.WriteAsync(payload, ct);
        await _stdin.FlushAsync(ct);
    }

    private async Task<JsonObject?> ReadAsync(CancellationToken ct)
    {
        await _readLock.WaitAsync(ct);
        try
        {
            int? length = null;
            var line = new StringBuilder();
            var one = new byte[1];
            while (true)
            {
                var n = await _stdout.ReadAsync(one.AsMemory(0, 1), ct);
                if (n == 0) return null;
                if (one[0] != (byte)'\n') { if (one[0] != (byte)'\r') line.Append((char)one[0]); continue; }
                if (line.Length == 0) break;
                const string contentLength = "Content-Length:";
                var header = line.ToString();
                line.Clear();
                if (header.StartsWith(contentLength, StringComparison.OrdinalIgnoreCase))
                    length = int.Parse(header[contentLength.Length..].Trim());
            }
            if (length is null) return null;

            var buffer = new byte[length.Value];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await _stdout.ReadAsync(buffer.AsMemory(read), ct);
                if (n == 0) return null;
                read += n;
            }
            return JsonNode.Parse(Encoding.UTF8.GetString(buffer)) as JsonObject;
        }
        finally { _readLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _stdin.Close();
                if (!_process.WaitForExit(5000)) _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) { _log("stopping the adapter: " + ex.Message); }
        _process.Dispose();
        await Task.CompletedTask;
    }
}
