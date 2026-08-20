using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using NetAndroidProfiler.Core.Sessions;
using NetAndroidProfiler.Core.Store;

namespace NetAndroidProfiler.Mcp;

/// <summary>
/// Owns the sessions created by this server process and resolves "the current
/// session" for tools that omit sessionId: the most recently created live
/// session, otherwise the newest session directory on disk.
/// </summary>
public sealed class SessionHost : IAsyncDisposable
{
    private readonly ILogger<SessionHost> _logger;
    private readonly ConcurrentDictionary<string, LiveSession> _live = new();
    private string? _currentId;

    public SessionHost(ILogger<SessionHost> logger)
    {
        _logger = logger;
        SessionsRoot = Environment.GetEnvironmentVariable("NAP_SESSIONS_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "net-android-profiler", "sessions");
        Directory.CreateDirectory(SessionsRoot);
    }

    public string SessionsRoot { get; }

    /// <summary>A session created in this process plus its background run task (when started with profile_start).</summary>
    public sealed class LiveSession
    {
        public required ProfilerSession Session { get; init; }
        public Task<SessionInfo>? RunTask { get; set; }
    }

    public LiveSession Create(SessionSpec spec)
    {
        var s = ProfilerSession.Create(spec, SessionsRoot);
        var live = new LiveSession { Session = s };
        _live[s.Id] = live;
        _currentId = s.Id;
        _logger.LogInformation("session {Id} created in {Dir}", s.Id, s.Directory);
        return live;
    }

    public LiveSession? Live(string? sessionId)
    {
        string? id = sessionId ?? _currentId;
        return id is not null && _live.TryGetValue(id, out var l) ? l : null;
    }

    /// <summary>Resolve the id for result queries: explicit id, else the current live session, else the newest ready session on disk.</summary>
    public string ResolveId(string? sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId)) return sessionId;
        if (_currentId is not null) return _currentId;
        var newest = ProfilerSession.ListSessions(SessionsRoot).FirstOrDefault(s => s.ready);
        if (newest.id is null) throw new McpException("No profiling session yet. Run profile_run first.");
        return newest.id;
    }

    /// <summary>Open the result store of a session (live Ready session or a session directory on disk).</summary>
    public ResultStore OpenResults(string? sessionId, out string id)
    {
        id = ResolveId(sessionId);
        if (_live.TryGetValue(id, out var live))
        {
            if (live.Session.State == SessionState.Ready) return ResultStore.Open(live.Session.DatabasePath);
            if (live.Session.State == SessionState.Failed) throw new McpException($"Session {id} failed: {live.Session.Info.Error}");
            throw new McpException($"Session {id} is still {live.Session.State}; wait for profile_stop / completion.");
        }
        string dir = Path.Combine(SessionsRoot, id);
        if (!Directory.Exists(dir)) throw new McpException($"Unknown session '{id}'. See profile_sessions.");
        if (!File.Exists(Path.Combine(dir, "session.db"))) throw new McpException($"Session '{id}' has no results (it did not complete).");
        return ProfilerSession.OpenResults(dir);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var l in _live.Values)
        {
            try { l.Session.Stop(); await l.Session.DisposeAsync(); } catch { }
        }
    }
}
