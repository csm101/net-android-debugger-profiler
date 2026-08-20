using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using NetAndroidProfiler.Core.Analysis;
using NetAndroidProfiler.Core.Devices;

namespace NetAndroidProfiler.Core.Collection;

/// <summary>Provider sets for the supported session kinds.</summary>
public static class ProviderSets
{
    // Same as dotnet-trace's cpu-sampling profile.
    public static IReadOnlyList<EventPipeProvider> Sampling() =>
    [
        new EventPipeProvider("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational, 0x0000F00000000000),
        new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, (long)ClrTraceEventParser.Keywords.Default),
    ];

    /// <summary>MonoProfiler enter/leave (+ allocations) plus runtime JIT events for in-session method names.</summary>
    public static IReadOnlyList<EventPipeProvider> Instrumenting(bool allocations) =>
    [
        new EventPipeProvider(MonoProfilerAnalyzer.ProviderName, EventLevel.Verbose,
            (long)(allocations ? MonoProfilerAnalyzer.Keywords.InstrumentingWithAllocations : MonoProfilerAnalyzer.Keywords.Instrumenting)),
        new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose, (long)(ClrTraceEventParser.Keywords.Jit | ClrTraceEventParser.Keywords.Loader)),
    ];

    /// <summary>GC heap dump (what dotnet-gcdump enables).</summary>
    public static IReadOnlyList<EventPipeProvider> HeapSnapshot() =>
    [
        new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose, (long)ClrTraceEventParser.Keywords.GCHeapSnapshot),
    ];
}

/// <summary>Live-heap snapshot aggregated per type.</summary>
public sealed record HeapSnapshot(DateTimeOffset TakenUtc, long TotalObjects, long TotalBytes, IReadOnlyList<(string typeName, long count, long bytes)> ByType);

/// <summary>
/// Runs EventPipe sessions against the Android runtime through the dsrouter IPC
/// endpoint (DiagnosticsClient on the dsrouter pid). Replaces dotnet-trace /
/// dotnet-gcdump as processes: same library underneath.
/// </summary>
public sealed class EventPipeCollector
{
    private readonly DiagnosticsClient _client;
    private readonly Action<string>? _log;

    public EventPipeCollector(int dsrouterPid, Action<string>? log = null)
    {
        _client = new DiagnosticsClient(dsrouterPid);
        _log = log;
    }

    /// <summary>Environment variable the session injects into the target app to recognize it on connect.</summary>
    public const string SessionMarkerVariable = "NAP_SESSION";

    /// <summary>
    /// Wait until the runtime is connected to dsrouter (the app may still be
    /// starting). Probes by asking for the process environment; retries until
    /// timeout. When <paramref name="expectedMarker"/> is given, the connected
    /// runtime must carry NAP_SESSION=&lt;marker&gt;: another .NET process that
    /// still points at the profiler port (typically an app profiled earlier and
    /// left running) would otherwise be profiled by mistake.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> WaitForRuntimeAsync(TimeSpan timeout, CancellationToken ct, string? expectedMarker = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var env = await Task.Run(() => _client.GetProcessEnvironment(), ct).ConfigureAwait(false);
                _log?.Invoke($"runtime connected ({env.Count} environment variables)");
                if (expectedMarker is not null)
                {
                    env.TryGetValue(SessionMarkerVariable, out var marker);
                    if (marker != expectedMarker)
                    {
                        env.TryGetValue("DOTNET_DiagnosticPorts", out var ports);
                        throw new ToolException(
                            "A different .NET process connected to the profiler port" +
                            (marker is null ? "" : $" (session marker {marker})") +
                            (ports is null ? "" : $" with DOTNET_DiagnosticPorts={ports}") +
                            ". Most likely an app profiled earlier is still running and keeps reconnecting: force-stop it " +
                            "(adb shell am force-stop <package>) and retry.");
                    }
                }
                return env;
            }
            catch (Exception e) when (e is ServerNotAvailableException or EndOfStreamException or IOException or TimeoutException or UnsupportedCommandException)
            {
                last = e;
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
        throw new ToolException($"No .NET runtime connected to dsrouter within {timeout}: {last?.Message}");
    }

    /// <summary>
    /// Collect a session to <paramref name="outputFile"/>: starts the session
    /// (retrying transient IPC failures), resumes a suspended runtime, streams
    /// until <paramref name="duration"/> elapses or <paramref name="stop"/> is
    /// cancelled, then stops the session (rundown included) and drains the stream.
    /// </summary>
    public async Task CollectToFileAsync(IReadOnlyList<EventPipeProvider> providers, string outputFile, TimeSpan? duration, CancellationToken stop, CancellationToken ct, bool resumeRuntime = true, int circularBufferMb = 256)
    {
        var session = await StartWithRetryAsync(providers, circularBufferMb, ct).ConfigureAwait(false);
        using (session)
        {
            if (resumeRuntime)
            {
                try { await Task.Run(() => _client.ResumeRuntime(), ct).ConfigureAwait(false); }
                catch (Exception e) when (e is UnsupportedCommandException or ServerErrorException) { _log?.Invoke($"resume runtime: {e.GetType().Name} (ignored: runtime not suspended)"); }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputFile))!);
            await using var file = new FileStream(outputFile, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, useAsync: true);
            var copy = session.EventStream.CopyToAsync(file, 1 << 16, ct);

            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stop, ct);
            if (duration is not null) waitCts.CancelAfter(duration.Value);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, waitCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* duration elapsed or stop requested */ }
            ct.ThrowIfCancellationRequested();

            _log?.Invoke("stopping session");
            try { await session.StopAsync(ct).ConfigureAwait(false); }
            catch (Exception e) when (e is EndOfStreamException or IOException) { _log?.Invoke($"stop: {e.Message} (runtime gone?)"); }
            await copy.ConfigureAwait(false);
            _log?.Invoke($"trace written: {outputFile} ({file.Length} bytes)");
        }
    }

    /// <summary>
    /// Take a heap snapshot: a GC heap dump session parsed live and aggregated
    /// per type. MonoVM does not signal the end of the dump (no prompt GCStop -
    /// dotnet-gcdump waits for its timeout too), so the session ends when no
    /// GCBulkNode event arrived for <paramref name="quiescence"/> after the first
    /// one. A session that produces no node within <paramref name="firstEventTimeout"/>
    /// (typical when the app was started a moment ago) is stopped and retried.
    /// </summary>
    public async Task<HeapSnapshot> TakeHeapSnapshotAsync(TimeSpan timeout, CancellationToken ct, TimeSpan? quiescence = null, TimeSpan? firstEventTimeout = null, int attempts = 3)
    {
        var quiet = quiescence ?? TimeSpan.FromSeconds(3);
        var firstWait = firstEventTimeout ?? TimeSpan.FromSeconds(20);
        var deadline = DateTime.UtcNow + timeout;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            var snap = await TakeHeapSnapshotOnceAsync(remaining, quiet, firstWait, attempt, ct).ConfigureAwait(false);
            if (snap is not null) return snap;
            _log?.Invoke($"heap snapshot attempt {attempt}: no heap dump events, retrying");
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        throw new ToolException("Heap snapshot produced no objects (GC heap dump events not received). The app may still be starting: retry in a few seconds.");
    }

    private async Task<HeapSnapshot?> TakeHeapSnapshotOnceAsync(TimeSpan timeout, TimeSpan quiet, TimeSpan firstWait, int attempt, CancellationToken ct)
    {
        var session = await StartWithRetryAsync(ProviderSets.HeapSnapshot(), 256, ct).ConfigureAwait(false);
        using (session)
        {
            var typeNames = new Dictionary<ulong, string>();
            var byType = new Dictionary<ulong, (long count, long bytes)>();
            long nodes = 0;
            long lastNodeTicks = 0;
            var taken = DateTimeOffset.UtcNow;
            var started = DateTime.UtcNow;

            using var source = new EventPipeEventSource(session.EventStream);
            source.Clr.TypeBulkType += ev =>
            {
                for (int i = 0; i < ev.Count; i++) { var v = ev.Values(i); typeNames[(ulong)v.TypeID] = v.TypeName; }
            };
            source.Clr.GCBulkNode += ev =>
            {
                for (int i = 0; i < ev.Count; i++)
                {
                    var v = ev.Values(i);
                    var cur = byType.GetValueOrDefault((ulong)v.TypeID);
                    byType[(ulong)v.TypeID] = (cur.count + 1, cur.bytes + (long)v.Size);
                }
                Interlocked.Add(ref nodes, ev.Count);
                Interlocked.Exchange(ref lastNodeTicks, DateTime.UtcNow.Ticks);
            };

            var processing = Task.Run(() => { try { source.Process(); } catch (Exception e) { _log?.Invoke($"heap parse ended: {e.Message}"); } }, ct);

            // Poll for completion: first node within firstWait, then quiescence.
            while (!processing.IsCompleted)
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                long n = Interlocked.Read(ref nodes);
                long lastTicks = Interlocked.Read(ref lastNodeTicks);
                if (n == 0 && now - started > firstWait) break;
                if (n > 0 && now.Ticks - lastTicks > quiet.Ticks) break;
                if (now - started > timeout) { _log?.Invoke("heap snapshot: overall timeout"); break; }
            }
            if (!processing.IsCompleted)
            {
                try { await session.StopAsync(ct).ConfigureAwait(false); } catch (Exception e) { _log?.Invoke($"heap session stop: {e.Message}"); }
                try { await processing.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); } catch (TimeoutException) { _log?.Invoke("heap parse did not finish after stop"); }
            }
            long total = Interlocked.Read(ref nodes);
            _log?.Invoke($"heap snapshot attempt {attempt}: {total} objects, {byType.Count} types, {(DateTime.UtcNow - started).TotalSeconds:F1}s");
            if (total == 0) return null;
            var list = byType
                .Select(kv => (typeNames.TryGetValue(kv.Key, out var nm) ? nm : $"<type 0x{kv.Key:X}>", kv.Value.count, kv.Value.bytes))
                .OrderByDescending(t => t.bytes).ToList();
            return new HeapSnapshot(taken, total, list.Sum(t => t.bytes), list);
        }
    }

    private async Task<EventPipeSession> StartWithRetryAsync(IReadOnlyList<EventPipeProvider> providers, int bufferMb, CancellationToken ct)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var s = await _client.StartEventPipeSessionAsync(providers, requestRundown: true, circularBufferMB: bufferMb, ct).ConfigureAwait(false);
                _log?.Invoke($"session started (attempt {attempt})");
                return s;
            }
            catch (Exception e) when (e is EndOfStreamException or ServerNotAvailableException or IOException or ServerErrorException)
            {
                last = e;
                _log?.Invoke($"session start attempt {attempt} failed: {e.GetType().Name}: {e.Message}");
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }
        throw new ToolException($"EventPipe session could not be started: {last?.Message}", last!);
    }
}
