using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using NetAndroidDebugger.Core;

namespace NetAndroidDebugger.Mcp;

/// <summary>
/// Holds the single active <see cref="DebugSession"/> of this server process and the
/// "current" pid/thread defaults derived from the last stop, so tools can omit them.
/// A session can exist before launch (NotStarted) so that breakpoints and exception
/// filters set up front are bound as soon as the app starts.
/// </summary>
public sealed class SessionHost(ILogger<SessionHost> logger)
{
    private readonly object _lock = new();
    private DebugSession? _session;

    public DebugSession? Current { get { lock (_lock) return _session; } }

    /// <summary>Returns the active (launched, not exited) session or throws a clear error for the caller.</summary>
    public DebugSession Require()
    {
        var s = Current;
        if (s is null || s.State is SessionState.NotStarted or SessionState.Exited)
            throw new McpException("No active debug session. Call launch_app first.");
        return s;
    }

    /// <summary>
    /// Returns a session usable for setup calls (breakpoints, exception filters): the current
    /// one if it is not exited, otherwise a fresh NotStarted session that the next launch_app
    /// will use.
    /// </summary>
    public DebugSession RequireForSetup()
    {
        lock (_lock)
        {
            if (_session is null || _session.State == SessionState.Exited)
                _session = NewSession();
            return _session;
        }
    }

    /// <summary>
    /// Session to launch with: the current NotStarted one (keeps breakpoints set up front),
    /// otherwise a fresh session replacing (and disposing) the previous one.
    /// </summary>
    public async Task<DebugSession> ForLaunchAsync(CancellationToken ct)
    {
        DebugSession? old = null;
        DebugSession s;
        lock (_lock)
        {
            if (_session is not null && _session.State == SessionState.NotStarted)
                return _session;
            old = _session;
            s = NewSession();
            _session = s;
        }
        if (old is not null)
        {
            try { await old.DisposeAsync(); }
            catch (Exception ex) { logger.LogWarning(ex, "disposing previous session"); }
        }
        return s;
    }

    public async Task CloseAsync(CancellationToken ct)
    {
        DebugSession? old;
        lock (_lock) { old = _session; _session = null; }
        if (old is not null) await old.DisposeAsync();
    }

    /// <summary>Resolves pid/thread defaults: explicit values win, otherwise the last stop's.</summary>
    public (int Pid, long ThreadId) ResolveTarget(DebugSession s, int? pid, long? threadId)
    {
        var last = s.LastStop;
        var p = pid ?? last?.Pid ?? throw new McpException("pid not given and no stop has happened yet. Wait for a stop, or pass pid/threadId.");
        var t = threadId ?? (last is not null && last.Pid == p ? last.ThreadId : throw new McpException($"threadId not given and pid {p} is not the last stopped process. Pass threadId."));
        return (p, t);
    }

    private DebugSession NewSession() => new(line => logger.LogInformation("{Line}", line));
}
