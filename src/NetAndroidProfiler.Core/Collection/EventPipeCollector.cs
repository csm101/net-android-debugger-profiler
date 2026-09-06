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

/// <summary>Result of streaming a trace to disk.</summary>
/// <param name="Bytes">Size of the written trace.</param>
/// <param name="StoppedBySizeLimit">True when the size limit, not the duration or the caller, ended the session.</param>
public sealed record TraceCollection(long Bytes, bool StoppedBySizeLimit);

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
                var env = await ProbeEnvironmentAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                if (env is null) continue;
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
    /// One environment request with a deadline. The router pairs each request with one of the
    /// runtime's connections, and on the emulator a connection has been seen whose reply only
    /// arrives when the app dies (right after a session that left the app reconnecting); a
    /// request that waited on it would block the probe for good. After the deadline the request
    /// is abandoned on its thread and the caller asks again on a new connection, which the
    /// router pairs with the runtime's next one.
    /// </summary>
    private async Task<Dictionary<string, string>?> ProbeEnvironmentAsync(TimeSpan deadline, CancellationToken ct)
    {
        var probe = new TaskCompletionSource<Dictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { probe.TrySetResult(_client.GetProcessEnvironment()); }
            catch (Exception e) { probe.TrySetException(e); }
        }) { IsBackground = true, Name = "nap-environment-probe" };
        thread.Start();
        var done = await Task.WhenAny(probe.Task, Task.Delay(deadline, ct)).ConfigureAwait(false);
        if (done == probe.Task) return await probe.Task.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        _log?.Invoke($"environment request unanswered after {deadline.TotalSeconds:F0}s: asking again on a new connection");
        return null;
    }

    /// <summary>
    /// Collect a session to <paramref name="outputFile"/>: starts the session
    /// (retrying transient IPC failures), resumes a suspended runtime, streams
    /// until <paramref name="duration"/> elapses or <paramref name="stop"/> is
    /// cancelled, then stops the session (rundown included) and drains the stream.
    /// </summary>
    /// <summary>
    /// Stream an EventPipe session to <paramref name="outputFile"/> until the duration
    /// elapses, the caller stops it, or the file reaches <paramref name="maxBytes"/>.
    /// </summary>
    /// <returns>How many bytes were written and whether the size limit ended the session.</returns>
    public async Task<TraceCollection> CollectToFileAsync(IReadOnlyList<EventPipeProvider> providers, string outputFile, TimeSpan? duration, CancellationToken stop, CancellationToken ct, bool resumeRuntime = true, int circularBufferMb = 256, long? maxBytes = null)
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
            using var copyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var copy = session.EventStream.CopyToAsync(file, 1 << 16, copyCts.Token);

            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stop, ct);
            if (duration is not null) waitCts.CancelAfter(duration.Value);
            bool hitLimit = false;
            try
            {
                if (maxBytes is null)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, waitCts.Token).ConfigureAwait(false);
                }
                else
                {
                    // A trace grows at the rate the app produces events (measured: ~30 KB/s
                    // sampling TestTarget, ~1.5 MB/s sampling a real app, ~0.75 MB/s
                    // instrumenting a busy callspec), so an open-ended session can fill a disk.
                    // Poll the file and end the session cleanly when it reaches the limit:
                    // what was collected so far is a valid trace and gets analyzed.
                    while (true)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(250), waitCts.Token).ConfigureAwait(false);
                        if (file.Length < maxBytes.Value) continue;
                        hitLimit = true;
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* duration elapsed or stop requested */ }
            ct.ThrowIfCancellationRequested();

            if (hitLimit) _log?.Invoke($"trace size limit reached ({maxBytes} bytes): stopping collection early");
            _log?.Invoke("stopping session");
            try { await session.StopAsync(ct).ConfigureAwait(false); }
            catch (Exception e) when (e is EndOfStreamException or IOException) { _log?.Invoke($"stop: {e.Message} (runtime gone?)"); }
            _log?.Invoke("stop acknowledged by the runtime, draining the event stream");
            await DrainAfterStopAsync(copy, file, copyCts, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            _log?.Invoke($"trace written: {outputFile} ({file.Length} bytes)");
            return new TraceCollection(file.Length, hitLimit);
        }
    }

    /// <summary>
    /// The runtime writes the rundown and closes its end of the stream before it acknowledges
    /// the stop, so whatever follows the acknowledgement is already in flight. dsrouter does
    /// not always propagate that end to the pipe (seen on the emulator: every byte it
    /// forwarded was in the file and the read stayed pending until cancelled), so the drain
    /// also ends when the file has stopped growing for <paramref name="quiet"/>.
    /// </summary>
    private async Task DrainAfterStopAsync(Task copy, FileStream file, CancellationTokenSource copyCts, TimeSpan quiet, CancellationToken ct)
    {
        long lastLength = file.Length;
        var lastGrowth = DateTime.UtcNow;
        while (true)
        {
            var done = await Task.WhenAny(copy, Task.Delay(500, ct)).ConfigureAwait(false);
            if (done == copy)
            {
                await copy.ConfigureAwait(false);
                return;
            }
            ct.ThrowIfCancellationRequested();
            if (file.Length != lastLength)
            {
                lastLength = file.Length;
                lastGrowth = DateTime.UtcNow;
                continue;
            }
            if (DateTime.UtcNow - lastGrowth < quiet) continue;
            _log?.Invoke($"event stream did not end within {quiet.TotalSeconds:F0}s of the stop: closing it with {file.Length} bytes");
            copyCts.Cancel();
            try { await copy.ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { }
            return;
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
