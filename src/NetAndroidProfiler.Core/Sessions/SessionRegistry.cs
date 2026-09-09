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

    /// <param name="spec">What to profile.</param>
    /// <param name="sessionsRoot">
    /// Where to put this one, when it does not belong with the others: a session kept
    /// beside the product it measures, or on a share somebody else reads. Empty means the
    /// registry's own root, which is the ordinary case.
    /// </param>
    public LiveSession Create(SessionSpec spec, string? sessionsRoot = null)
    {
        var session = ProfilerSession.Create(spec, string.IsNullOrWhiteSpace(sessionsRoot) ? SessionsRoot : sessionsRoot);
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

    /// <summary>
    /// Where a session lives: an id under this registry's root, or the full path of a
    /// session directory, which is how one created somewhere else is reached.
    /// </summary>
    public string DirectoryOf(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ProfilerException("A session id is required.");
        string dir = Path.IsPathRooted(sessionId) ? sessionId : Path.Combine(SessionsRoot, sessionId);
        if (!Directory.Exists(dir)) throw new ProfilerException($"Unknown session '{sessionId}'.");
        return dir;
    }

    /// <summary>Name a session on disk, or take its name away (null).</summary>
    public void Rename(string sessionId, string? name) => ProfilerSession.Rename(DirectoryOf(sessionId), name);

    /// <summary>
    /// Delete a session and everything it recorded. A session this process is still
    /// running is refused: stopping it is a decision of its own, and deleting the files
    /// under a collector that is writing them is how a device is left with the app's
    /// environment still redirected.
    /// </summary>
    public void Delete(string sessionId)
    {
        string dir = DirectoryOf(sessionId);
        string id = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (_live.TryGetValue(id, out var live)
            && live.Session.State is not (SessionState.Ready or SessionState.Failed or SessionState.Idle))
            throw new ProfilerException($"Session {id} is still {live.Session.State}: stop it before deleting it.");
        ProfilerSession.Delete(dir);
        _live.TryRemove(id, out _);
        if (_currentId == id) _currentId = null;
        _log?.Invoke($"session {id} deleted");
    }

    /// <summary>
    /// Open the result store of a session: a live Ready session, a session directory on
    /// disk, or the path of a result database - which is how an archived Get Results is
    /// read back, since an archive is an ordinary result database that outlives its
    /// session (<see cref="ProfilerSession.ArchiveAsync"/>).
    /// </summary>
    public ResultStore OpenResults(string? sessionId, out string id)
    {
        id = ResolveId(sessionId);
        if (id.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(id)) throw new ProfilerException($"No result database at '{id}'.");
            return ResultStore.Open(id);
        }
        if (_live.TryGetValue(id, out var live))
        {
            if (live.Session.State == SessionState.Ready) return ResultStore.Open(live.Session.DatabasePath);
            if (live.Session.State == SessionState.Failed) throw new ProfilerException($"Session {id} failed: {live.Session.Info.Error}");
            // A Get Results writes the database of a session that is still collecting, and reading
            // it back is the whole point of having asked for it: the results up to now.
            if (File.Exists(live.Session.DatabasePath)) return ResultStore.Open(live.Session.DatabasePath);
            throw new ProfilerException(
                $"Session {id} is still {live.Session.State} and has written no results yet; " +
                "profile_snapshot writes what it has so far, profile_stop ends it.");
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
