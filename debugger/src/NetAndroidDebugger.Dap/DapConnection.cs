using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetAndroidDebugger.Dap;

/// <summary>
/// The Debug Adapter Protocol wire format: <c>Content-Length: N\r\n\r\n</c> followed by N bytes of
/// UTF-8 JSON, over a pair of streams (stdin/stdout in normal use). Hand-rolled on purpose - the
/// framing is a dozen lines, and it keeps the adapter free of a debug-protocol dependency whose
/// licence would have to be cleared.
/// </summary>
public sealed class DapConnection(Stream input, Stream output)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _nextSeq;

    /// <summary>Reads one message, or null at end of stream.</summary>
    public async Task<JsonObject?> ReadAsync(CancellationToken ct)
    {
        var length = await ReadHeaderAsync(ct).ConfigureAwait(false);
        if (length is null) return null;

        var buffer = new byte[length.Value];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await input.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) return null;
            read += n;
        }
        return JsonNode.Parse(Encoding.UTF8.GetString(buffer)) as JsonObject;
    }

    /// <summary>Reads the header block and returns the announced body length, or null at EOF.</summary>
    private async Task<int?> ReadHeaderAsync(CancellationToken ct)
    {
        int? length = null;
        var line = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            var n = await input.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0) return null;
            if (one[0] != (byte)'\n') { if (one[0] != (byte)'\r') line.Append((char)one[0]); continue; }

            if (line.Length == 0) return length;  // blank line: end of the header block
            var header = line.ToString();
            line.Clear();
            const string contentLength = "Content-Length:";
            if (header.StartsWith(contentLength, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(header[contentLength.Length..].Trim(), out var parsed))
                length = parsed;
        }
    }

    public Task SendResponseAsync(JsonObject request, JsonObject? body, CancellationToken ct)
        => SendAsync(Response(request, success: true, body, message: null), ct);

    public Task SendErrorAsync(JsonObject request, string message, CancellationToken ct)
        => SendAsync(Response(request, success: false, body: null, message), ct);

    public Task SendEventAsync(string @event, JsonObject? body, CancellationToken ct)
    {
        var msg = new JsonObject { ["type"] = "event", ["event"] = @event };
        if (body is not null) msg["body"] = body;
        return SendAsync(msg, ct);
    }

    private static JsonObject Response(JsonObject request, bool success, JsonObject? body, string? message)
    {
        var msg = new JsonObject
        {
            ["type"] = "response",
            ["request_seq"] = request["seq"]?.GetValue<int>() ?? 0,
            ["success"] = success,
            ["command"] = request["command"]?.GetValue<string>() ?? "",
        };
        if (message is not null) msg["message"] = message;
        if (body is not null) msg["body"] = body;
        return msg;
    }

    private async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        message["seq"] = Interlocked.Increment(ref _nextSeq);
        var payload = Encoding.UTF8.GetBytes(message.ToJsonString(Json));
        var header = Encoding.UTF8.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");

        // One writer at a time: events are raised from engine threads while a response is in flight.
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await output.WriteAsync(header, ct).ConfigureAwait(false);
            await output.WriteAsync(payload, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }
}
