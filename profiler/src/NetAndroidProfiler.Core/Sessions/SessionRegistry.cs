using NetAndroidProfiler.Core.Store;

namespace NetAndroidProfiler.Core.Sessions;

/// <summary>
/// Owns the sessions created by one process and resolves "the current session" for
/// callers that do not name one: the most recently created live session, otherwise the
/// newest session directory on disk. Frontend-neutral - the MCP server and the local
/// control service both sit on this.
/// </summary>
public sealed class SessionRegistry : IAsyncDisposable
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, LiveSession> _live = new();
    private readonly Action<string>? _log;
    private string? _currentId;

    /// <param name="sessionsRoot">Where session directories live; defaults to NAP_SESSIONS_ROOT or LocalApplicationData.</param>
    /// <param name="log">Optional progress sink (the frontends log differently).</param>
    public SessionRegistry(string? sessionsRoot = null, Action<string>? log = null)
    {
        _log = log;
        SessionsRoot = sessionsRoot
            ?? Environment.GetEnvironmentVariable("NAP_SESSIONS_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "net-android-profiler", "sessions");
        Directory.CreateDirectory(SessionsRoot);
    }

    public string SessionsRoot { get; }

    /// <summary>A session created in this process plus its background run task (when it was started without waiting).</summary>
    public sealed class LiveSession
    {
        public required ProfilerSession Session { get; init; }
        public Task<SessionInfo>? RunTask { get; set; }
    }

    /// <summary>Sessions created in this process, newest first.</summary>
    public IReadOnlyList<LiveSession> LiveSessions => _live.Values.OrderByDescending(l => l.Session.CreatedUtc).ToList();

    public LiveSession Create(SessionSpec spec)
    {
        var session = ProfilerSession.Create(spec, SessionsRoot);
        var live = new LiveSession { Session = session };
        _live[session.Id] = live;
        _currentId = session.Id;
        _log?.Invoke($"session {session.Id} created in {session.Directory}");
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
        if (newest.id is null) throw new ProfilerException("No profiling session yet: run one first.");
        return newest.id;
    }

    /// <summary>Open the result store of a session (a live Ready session or a session directory on disk).</summary>
    public ResultStore OpenResults(string? sessionId, out string id)
    {
        id = ResolveId(sessionId);
        if (_live.TryGetValue(id, out var live))
        {
            if (live.Session.State == SessionState.Ready) return ResultStore.Open(live.Session.DatabasePath);
            if (live.Session.State == SessionState.Failed) throw new ProfilerException($"Session {id} failed: {live.Session.Info.Error}");
            throw new ProfilerException($"Session {id} is still {live.Session.State}; wait for it to finish.");
        }
        string dir = Path.Combine(SessionsRoot, id);
        if (!Directory.Exists(dir)) throw new ProfilerException($"Unknown session '{id}'.");
        if (!File.Exists(Path.Combine(dir, "session.db"))) throw new ProfilerException($"Session '{id}' has no results (it did not complete).");
        return ProfilerSession.OpenResults(dir);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var l in _live.Values)
        {
            try { l.Session.Stop(); await l.Session.DisposeAsync().ConfigureAwait(false); } catch { /* shutting down */ }
        }
    }
}
