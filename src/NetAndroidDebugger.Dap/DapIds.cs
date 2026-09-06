using System.Collections.Concurrent;

namespace NetAndroidDebugger.Dap;

/// <summary>
/// DAP identifies threads, frames and variables with plain integers, while the engine speaks in
/// (pid, thread id), (pid, thread id, frame index) and opaque expansion handles. This keeps the
/// two-way mapping. Ids are never reused, so a stale id from the client is refused instead of
/// silently addressing something else.
/// </summary>
public sealed class DapIds
{
    private readonly ConcurrentDictionary<(int Pid, long ThreadId), int> _threadIds = new();
    private readonly ConcurrentDictionary<int, (int Pid, long ThreadId)> _threads = new();
    private readonly ConcurrentDictionary<int, (int Pid, long ThreadId, int Frame)> _frames = new();
    private readonly ConcurrentDictionary<int, VariableRef> _variables = new();
    private int _next;

    /// <summary>What a DAP variablesReference points at: a frame's locals, or a value's children.</summary>
    public readonly record struct VariableRef(int Pid, long ThreadId, int Frame, string? ExpansionHandle);

    public int ThreadId(int pid, long threadId)
        => _threadIds.GetOrAdd((pid, threadId), key =>
        {
            var id = Interlocked.Increment(ref _next);
            _threads[id] = key;
            return id;
        });

    public bool TryThread(int id, out (int Pid, long ThreadId) target) => _threads.TryGetValue(id, out target);

    public int FrameId(int pid, long threadId, int frame)
    {
        var id = Interlocked.Increment(ref _next);
        _frames[id] = (pid, threadId, frame);
        return id;
    }

    public bool TryFrame(int id, out (int Pid, long ThreadId, int Frame) target) => _frames.TryGetValue(id, out target);

    public int VariableId(VariableRef r)
    {
        var id = Interlocked.Increment(ref _next);
        _variables[id] = r;
        return id;
    }

    public bool TryVariable(int id, out VariableRef r) => _variables.TryGetValue(id, out r);

    /// <summary>
    /// Drops frame and variable ids: they address a suspended process and mean nothing once it
    /// resumes. Thread ids survive, because DAP clients keep showing the same thread list.
    /// </summary>
    public void InvalidateStopScoped()
    {
        _frames.Clear();
        _variables.Clear();
    }
}
