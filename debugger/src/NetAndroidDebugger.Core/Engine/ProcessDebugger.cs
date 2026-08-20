using System.Net;
using Mono.Debugging.Client;
using Mono.Debugging.Soft;

namespace NetAndroidDebugger.Core.Engine;

/// <summary>
/// One <see cref="SoftDebuggerSession"/> bound to one debuggee process (Android pid).
/// Translates Mono.Debugging events into simple callbacks; owns no cross-process state.
/// All callbacks are raised on Mono.Debugging's event thread.
/// </summary>
internal sealed class ProcessDebugger : IDisposable
{
    private readonly BreakpointStore _store;
    private readonly DebuggerSessionOptions _options;
    private readonly Action<string> _log;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _pauseRequested;
    private bool _disposed;

    public ProcessDebugger(int pid, string name, int port, BreakpointStore store, DebuggerSessionOptions options, Action<string> log)
    {
        Pid = pid;
        Name = name;
        Port = port;
        _store = store;
        _options = options;
        _log = log;
        Session = new SoftDebuggerSession();
    }

    public int Pid { get; }
    public string Name { get; }
    public int Port { get; }
    public SoftDebuggerSession Session { get; }

    public bool IsStopped { get; private set; }
    public bool HasExited { get; private set; }
    public bool IsConnected => _ready.Task.IsCompletedSuccessfully && !HasExited;

    /// <summary>Thread and backtrace delivered with the last stop event (null while running).</summary>
    public ThreadInfo? StopThread { get; private set; }
    public Backtrace? StopBacktrace { get; private set; }
    public TargetEventArgs? LastStopArgs { get; private set; }
    public StopReason LastStopReason { get; private set; }

    public event Action<ProcessDebugger>? Stopped;
    public event Action<ProcessDebugger>? Resumed;
    public event Action<ProcessDebugger>? Exited;
    public event Action<ProcessDebugger, bool, string>? Output;

    public async Task ConnectAsync(TimeSpan timeout, CancellationToken ct)
    {
        var s = Session;
        s.ExceptionHandler = ex =>
        {
            _log($"[pid {Pid}] Mono.Debugging: {ex.GetType().Name}: {ex.Message}");
            if (!_ready.Task.IsCompleted)
                _ready.TrySetException(ex);
            return true;
        };
        s.LogWriter = (isErr, text) => _log($"[pid {Pid}] {text.TrimEnd()}");
        s.OutputWriter = (isErr, text) => Output?.Invoke(this, isErr, text);
        // Debuggee traces (Debug.WriteLine / Console via the agent's UserLog events) are app
        // output, not debugger log; without DebugWriter Mono.Debugging folds them into LogWriter.
        s.DebugWriter = (level, category, message) =>
            Output?.Invoke(this, false, string.IsNullOrEmpty(category) ? message : $"[{category}] {message}");
        s.TargetEvent += OnTargetEvent;
        s.Breakpoints = _store;

        var args = new SoftDebuggerConnectArgs(Name, IPAddress.Loopback, Port)
        {
            // The agent logs "Trying to initialize" slightly before it is bound; retry briefly.
            MaxConnectionAttempts = 40,
            TimeBetweenConnectionAttempts = 250,
        };
        _log($"[pid {Pid}] connecting to 127.0.0.1:{Port} ({Name})");
        s.Run(new SoftDebuggerStartInfo(args), _options);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _ready.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LaunchException($"pid {Pid}: no SDB handshake on port {Port} within {timeout.TotalSeconds:F0}s");
        }
        _log($"[pid {Pid}] connected");
    }

    private void OnTargetEvent(object? sender, TargetEventArgs e)
    {
        switch (e.Type)
        {
            case TargetEventType.TargetReady:
                _ready.TrySetResult(true);
                break;

            case TargetEventType.TargetHitBreakpoint:
                MarkStopped(e, StopReason.Breakpoint);
                break;
            case TargetEventType.TargetStopped:
            case TargetEventType.TargetInterrupted:
                MarkStopped(e, _pauseRequested ? StopReason.Pause : StopReason.Step);
                break;
            case TargetEventType.ExceptionThrown:
                MarkStopped(e, StopReason.Exception);
                break;
            case TargetEventType.UnhandledException:
                MarkStopped(e, StopReason.UnhandledException);
                break;

            case TargetEventType.TargetExited:
                HasExited = true;
                IsStopped = false;
                if (!_ready.Task.IsCompleted)
                    _ready.TrySetException(new LaunchException($"pid {Pid}: target exited before the handshake completed"));
                Exited?.Invoke(this);
                break;

            case TargetEventType.ThreadStarted:
            case TargetEventType.ThreadStopped:
            case TargetEventType.TargetSignaled:
            default:
                break;
        }
    }

    private void MarkStopped(TargetEventArgs e, StopReason reason)
    {
        _pauseRequested = false;
        IsStopped = true;
        StopThread = e.Thread;
        StopBacktrace = e.Backtrace;
        LastStopArgs = e;
        LastStopReason = reason;
        Stopped?.Invoke(this);
    }

    private void MarkResumed()
    {
        IsStopped = false;
        StopThread = null;
        StopBacktrace = null;
        LastStopArgs = null;
        Resumed?.Invoke(this);
    }

    public void Continue()
    {
        if (!IsStopped) return;
        MarkResumed();
        Session.Continue();
    }

    public void Pause()
    {
        if (IsStopped || HasExited) return;
        _pauseRequested = true;
        Session.Stop();
    }

    public void StepOver(long threadId) => Step(threadId, Session.NextLine);
    public void StepInto(long threadId) => Step(threadId, Session.StepLine);
    public void StepOut(long threadId) => Step(threadId, Session.Finish);

    private void Step(long threadId, Action step)
    {
        if (!IsStopped) throw new InvalidSessionStateException($"pid {Pid} is not stopped");
        var thread = FindThread(threadId) ?? throw new ArgumentException($"pid {Pid}: no thread {threadId}");
        thread.SetActive();
        MarkResumed();
        step();
    }

    public ThreadInfo[] GetThreads()
    {
        var proc = Session.GetProcesses().FirstOrDefault();
        return proc?.GetThreads() ?? [];
    }

    public ThreadInfo? FindThread(long threadId)
    {
        if (StopThread is not null && StopThread.Id == threadId) return StopThread;
        return GetThreads().FirstOrDefault(t => t.Id == threadId);
    }

    public Backtrace? GetBacktrace(long threadId)
    {
        if (StopThread is not null && StopThread.Id == threadId && StopBacktrace is not null)
            return StopBacktrace;
        return FindThread(threadId)?.Backtrace;
    }

    public void Detach()
    {
        try { Session.Detach(); }
        catch (Exception ex) { _log($"[pid {Pid}] detach: {ex.Message}"); }
    }

    public void Exit()
    {
        try { Session.Exit(); }
        catch (Exception ex) { _log($"[pid {Pid}] exit: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Session.Dispose(); }
        catch (Exception ex) { _log($"[pid {Pid}] dispose: {ex.Message}"); }
    }
}
