using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using NetAndroidProfiler.Core.Sessions;
using NetAndroidProfiler.Core.Store;

namespace NetAndroidProfiler.Mcp;

/// <summary>
/// The MCP frontend's view of <see cref="SessionRegistry"/>: same sessions, with Core's
/// failures translated into MCP errors so the client sees a protocol error instead of a
/// crashed tool.
/// </summary>
public sealed class SessionHost : IAsyncDisposable
{
    private readonly SessionRegistry _registry;

    public SessionHost(ILogger<SessionHost> logger)
    {
        _registry = new SessionRegistry(log: line => logger.LogInformation("{Line}", line));
    }

    public string SessionsRoot => _registry.SessionsRoot;

    public SessionRegistry.LiveSession Create(SessionSpec spec) => _registry.Create(spec);

    /// <summary>The sessions this process created that are still alive, newest first.</summary>
    public IReadOnlyList<SessionRegistry.LiveSession> LiveSessions => _registry.LiveSessions;

    public SessionRegistry.LiveSession? Live(string? sessionId) => _registry.Live(sessionId);

    public string ResolveId(string? sessionId)
    {
        try { return _registry.ResolveId(sessionId); }
        catch (ProfilerException e) { throw new McpException(e.Message); }
    }

    public ResultStore OpenResults(string? sessionId, out string id)
    {
        try { return _registry.OpenResults(sessionId, out id); }
        catch (ProfilerException e) { throw new McpException(e.Message); }
    }

    public void Rename(string sessionId, string? name)
    {
        try { _registry.Rename(sessionId, name); }
        catch (ProfilerException e) { throw new McpException(e.Message); }
    }

    public void Delete(string sessionId)
    {
        try { _registry.Delete(sessionId); }
        catch (ProfilerException e) { throw new McpException(e.Message); }
    }

    public ValueTask DisposeAsync() => _registry.DisposeAsync();
}
