using Mono.Debugging.Client;
using NetAndroidDebugger.Core.Adb;
using NetAndroidDebugger.Core.Launch;

namespace NetAndroidDebugger.Core;

/// <summary>
/// Frontend-neutral facade over one debugged Android application. An application is a
/// set of processes (main + helpers); each one is debugged by its own soft-debugger
/// session, aggregated here behind a single state machine and a monotonic stop
/// generation counter. All public members are thread-safe. No MCP/DAP/JSON types.
/// </summary>
public sealed class DebugSession : IAsyncDisposable
{
    private const int MaxOutputLines = 5000;

    private readonly object _lock = new();
    private readonly Action<string> _log;
    private readonly BreakpointStore _store = new();
    private readonly Dictionary<int, ProcessDebuggerEntry> _processes = new();
    private readonly Dictionary<int, (BreakpointSpec Spec, Breakpoint Bp)> _breakpoints = new();
    private readonly List<string> _appOutput = new();
    private readonly List<string> _debuggerOutput = new();
    private readonly Dictionary<string, (int Pid, ObjectValue Value)> _values = new();
    private readonly DebuggerSessionOptions _sessionOptions;

    private TaskCompletionSource<bool> _changed = NewSignal();
    private SessionState _state = SessionState.NotStarted;
    private long _generation;
    private StopEvent? _lastStop;
    private string? _lastError;
    private int _nextBreakpointId;
    private long _nextValueId;
    private ExceptionFilters _exceptionFilters = new();

    private AdbClient? _adb;
    private AndroidLauncher? _launcher;
    private AppTarget? _app;
    private LaunchOptions? _launchOptions;

    private sealed class ProcessDebuggerEntry(Engine.ProcessDebugger debugger)
    {
        public Engine.ProcessDebugger Debugger { get; } = debugger;
        public bool Died { get; set; }
    }

    public DebugSession(Action<string>? logSink = null)
    {
        _log = line =>
        {
            lock (_lock)
            {
                _debuggerOutput.Add(line);
                if (_debuggerOutput.Count > MaxOutputLines) _debuggerOutput.RemoveRange(0, _debuggerOutput.Count - MaxOutputLines);
            }
            logSink?.Invoke(line);
        };
        _sessionOptions = new DebuggerSessionOptions
        {
            EvaluationOptions = EvaluationOptions.DefaultOptions,
            ProjectAssembliesOnly = false,
        };
        _sessionOptions.EvaluationOptions.EvaluationTimeout = 3000;
        _sessionOptions.EvaluationOptions.MemberEvaluationTimeout = 5000;
    }

    /// <summary>Raised after every state transition (on the thread that caused it).</summary>
    public event EventHandler<SessionState>? StateChanged;

    /// <summary>Raised for every stop of any debuggee process.</summary>
    public event EventHandler<StopEvent>? Stopped;

    public SessionState State { get { lock (_lock) return _state; } }
    public long StopGeneration { get { lock (_lock) return _generation; } }
    public StopEvent? LastStop { get { lock (_lock) return _lastStop; } }

    // ------------------------------------------------------------------ lifecycle

    public Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken ct, string adbPath = "adb")
        => new AdbClient(adbPath).ListDevicesAsync(ct);

    /// <summary>
    /// Deploys (optionally), starts the app with the debugger agent enabled and attaches
    /// to its main process. Helper processes are attached automatically as they appear.
    /// </summary>
    public async Task LaunchAsync(AppTarget app, LaunchOptions options, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_state != SessionState.NotStarted)
                throw new InvalidSessionStateException($"LaunchAsync is only valid in NotStarted (current: {_state})");
            _app = app;
            _launchOptions = options;
        }

        _adb = new AdbClient(options.AdbPath);
        var launcher = new AndroidLauncher(_adb, app, options, _log);
        _launcher = launcher;
        launcher.AppOutput += (pid, line) => AppendAppOutput(line);
        launcher.ProcessDied += OnProcessDied;
        launcher.AgentDetected += ready => _ = AttachProcessSafeAsync(ready);

        try
        {
            if (options.Deploy)
            {
                SetState(SessionState.Deploying);
                await launcher.DeployAsync(ct).ConfigureAwait(false);
            }
            SetState(SessionState.Launching);
            var first = await launcher.LaunchAsync(ct).ConfigureAwait(false);
            // The launcher also raises AgentDetected for the first agent; AttachProcessAsync
            // is idempotent per pid, so whichever path runs first wins.
            await AttachProcessAsync(first, ct).ConfigureAwait(false);
            SetState(SessionState.Running);
        }
        catch (Exception ex)
        {
            lock (_lock) _lastError = ex.Message;
            _log($"launch failed: {ex}");
            await ShutdownCoreAsync(terminate: true, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task AttachProcessSafeAsync(AgentReady ready)
    {
        try { await AttachProcessAsync(ready, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _log($"attach to pid {ready.Pid} failed: {ex.Message}");
            lock (_lock) _lastError = ex.Message;
        }
    }

    private async Task AttachProcessAsync(AgentReady ready, CancellationToken ct)
    {
        Engine.ProcessDebugger pd;
        lock (_lock)
        {
            if (_processes.ContainsKey(ready.Pid)) return;
            if (_state == SessionState.Exited) return;
            pd = new Engine.ProcessDebugger(ready.Pid, ready.ProcessName ?? _app!.PackageName, ready.Port, _store, _sessionOptions, _log);
            _processes[ready.Pid] = new ProcessDebuggerEntry(pd);
        }
        pd.Stopped += OnProcessStopped;
        pd.Resumed += OnProcessResumed;
        pd.Exited += OnProcessExited;
        pd.Output += (p, isErr, text) => AppendAppOutput($"[pid {p.Pid}] {text.TrimEnd()}");
        try
        {
            await pd.ConnectAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        }
        catch
        {
            lock (_lock) _processes.Remove(ready.Pid);
            pd.Dispose();
            throw;
        }
        Signal();
    }

    /// <summary>Ends the session. On Mono Android a detach terminates the app anyway, so detach == terminate.</summary>
    public Task DetachAsync(CancellationToken ct) => ShutdownCoreAsync(terminate: true, ct);

    /// <summary>Terminates the app and ends the session.</summary>
    public Task TerminateAsync(CancellationToken ct) => ShutdownCoreAsync(terminate: true, ct);

    private async Task ShutdownCoreAsync(bool terminate, CancellationToken ct)
    {
        Engine.ProcessDebugger[] procs;
        lock (_lock)
        {
            if (_state == SessionState.Exited && _launcher is null) return;
            procs = _processes.Values.Select(e => e.Debugger).ToArray();
        }
        foreach (var p in procs)
        {
            if (terminate) p.Exit(); else p.Detach();
            p.Dispose();
        }
        var launcher = _launcher;
        _launcher = null;
        if (launcher is not null)
        {
            try { await launcher.ShutdownAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { _log($"shutdown: {ex.Message}"); }
            await launcher.DisposeAsync().ConfigureAwait(false);
        }
        lock (_lock)
        {
            _processes.Clear();
            _values.Clear();
        }
        SetState(SessionState.Exited);
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownCoreAsync(terminate: true, CancellationToken.None).ConfigureAwait(false);
    }

    public SessionStatus GetStatus()
    {
        lock (_lock)
        {
            return new SessionStatus(_state, _generation, _lastStop, _launchOptions?.DeviceSerial, _app?.PackageName, SnapshotProcessesNoLock(), _lastError);
        }
    }

    public IReadOnlyList<ProcessSnapshot> GetProcesses()
    {
        lock (_lock) return SnapshotProcessesNoLock();
    }

    private IReadOnlyList<ProcessSnapshot> SnapshotProcessesNoLock()
        => _processes.Values.Select(e => new ProcessSnapshot(e.Debugger.Pid, e.Debugger.Name, e.Debugger.Port, e.Debugger.IsStopped, e.Debugger.HasExited || e.Died)).ToList();

    // ------------------------------------------------------------------ events from processes

    private void OnProcessStopped(Engine.ProcessDebugger pd)
    {
        StopEvent ev;
        lock (_lock)
        {
            _generation++;
            var loc = TryDescribeLocation(pd.StopBacktrace);
            string? exType = null, message = null;
            if (pd.LastStopReason is StopReason.Exception or StopReason.UnhandledException)
            {
                try
                {
                    var ei = pd.StopBacktrace?.FrameCount > 0 ? pd.StopBacktrace.GetFrame(0).GetException() : null;
                    exType = ei?.Type;
                    message = ei?.Message;
                }
                catch (Exception ex) { message = $"(exception details unavailable: {ex.Message})"; }
            }
            ev = new StopEvent(_generation, pd.Pid, pd.LastStopReason, pd.StopThread?.Id ?? 0, loc, exType, message);
            _lastStop = ev;
            InvalidateValuesNoLock(pd.Pid);
            _state = SessionState.Stopped;
        }
        _log($"stop #{ev.Generation}: pid {ev.Pid} {ev.Reason} thread {ev.ThreadId} at {ev.Location?.Method} {ev.Location?.File}:{ev.Location?.Line}");
        Signal();
        StateChanged?.Invoke(this, SessionState.Stopped);
        Stopped?.Invoke(this, ev);
    }

    private void OnProcessResumed(Engine.ProcessDebugger pd)
    {
        bool anyStopped;
        lock (_lock)
        {
            InvalidateValuesNoLock(pd.Pid);
            anyStopped = _processes.Values.Any(e => e.Debugger.IsStopped);
            if (!anyStopped && _state == SessionState.Stopped)
                _state = SessionState.Running;
        }
        if (!anyStopped) StateChanged?.Invoke(this, SessionState.Running);
    }

    private void OnProcessExited(Engine.ProcessDebugger pd)
    {
        _log($"pid {pd.Pid} ({pd.Name}) exited");
        bool allGone;
        lock (_lock)
        {
            InvalidateValuesNoLock(pd.Pid);
            allGone = _processes.Values.All(e => e.Debugger.HasExited || e.Died);
            if (allGone && _state != SessionState.Exited && _state != SessionState.NotStarted)
            {
                _generation++;
            }
        }
        Signal();
        if (allGone)
        {
            // Do not block the Mono event thread on adb work.
            _ = Task.Run(() => ShutdownCoreAsync(terminate: true, CancellationToken.None));
        }
    }

    private void OnProcessDied(int pid)
    {
        lock (_lock)
        {
            if (_processes.TryGetValue(pid, out var e)) e.Died = true;
        }
    }

    // ------------------------------------------------------------------ execution control

    /// <summary>Resumes every stopped process and waits for the next stop (or exit). Returns null on timeout.</summary>
    public async Task<StopEvent?> ContinueAndWaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        var gen = StopGeneration;
        Continue();
        return await WaitForStopAsync(gen, timeout, ct).ConfigureAwait(false);
    }

    /// <summary>Resumes every stopped process without waiting.</summary>
    public void Continue()
    {
        foreach (var pd in StoppedProcesses())
            pd.Continue();
    }

    /// <summary>Waits until the stop generation exceeds <paramref name="afterGeneration"/> or the session exits. Returns null on timeout.</summary>
    public async Task<StopEvent?> WaitForStopAsync(long afterGeneration, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            Task signal;
            lock (_lock)
            {
                if (_generation > afterGeneration)
                    return _state == SessionState.Stopped ? _lastStop : null;
                if (_state == SessionState.Exited)
                    return null;
                signal = _changed.Task;
            }
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return null;
            try { await signal.WaitAsync(remaining, ct).ConfigureAwait(false); }
            catch (TimeoutException) { return null; }
        }
    }

    /// <summary>
    /// Returns the current stop immediately when the session is already stopped, otherwise
    /// waits for the next stop. This is what a caller that has not seen any stop yet wants.
    /// </summary>
    public Task<StopEvent?> WaitForCurrentOrNextStopAsync(TimeSpan timeout, CancellationToken ct)
    {
        long after;
        lock (_lock)
        {
            if (_state == SessionState.Stopped && _lastStop is not null)
                return Task.FromResult<StopEvent?>(_lastStop);
            after = _generation;
        }
        return WaitForStopAsync(after, timeout, ct);
    }

    /// <summary>Suspends every running process and waits for the stop.</summary>
    public async Task<StopEvent?> PauseAsync(TimeSpan timeout, CancellationToken ct)
    {
        var gen = StopGeneration;
        Engine.ProcessDebugger[] running;
        lock (_lock) running = _processes.Values.Select(e => e.Debugger).Where(p => !p.IsStopped && !p.HasExited).ToArray();
        if (running.Length == 0)
            throw new InvalidSessionStateException("no running process to pause");
        foreach (var p in running) p.Pause();
        return await WaitForStopAsync(gen, timeout, ct).ConfigureAwait(false);
    }

    public Task<StopEvent?> StepOverAsync(int pid, long threadId, TimeSpan timeout, CancellationToken ct)
        => StepAsync(pid, threadId, timeout, ct, (p, t) => p.StepOver(t));
    public Task<StopEvent?> StepIntoAsync(int pid, long threadId, TimeSpan timeout, CancellationToken ct)
        => StepAsync(pid, threadId, timeout, ct, (p, t) => p.StepInto(t));
    public Task<StopEvent?> StepOutAsync(int pid, long threadId, TimeSpan timeout, CancellationToken ct)
        => StepAsync(pid, threadId, timeout, ct, (p, t) => p.StepOut(t));

    private async Task<StopEvent?> StepAsync(int pid, long threadId, TimeSpan timeout, CancellationToken ct, Action<Engine.ProcessDebugger, long> step)
    {
        var pd = RequireStoppedProcess(pid);
        var gen = StopGeneration;
        step(pd, threadId);
        return await WaitForStopAsync(gen, timeout, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ breakpoints

    public BreakpointInfo SetBreakpoint(BreakpointSpec spec)
    {
        lock (_lock)
        {
            var bp = _store.Add(spec.File, spec.Line);
            if (!string.IsNullOrWhiteSpace(spec.Condition))
                bp.ConditionExpression = spec.Condition;
            if (spec.HitCount > 0)
            {
                bp.HitCountMode = HitCountMode.GreaterThanOrEqualTo;
                bp.HitCount = spec.HitCount;
            }
            var id = ++_nextBreakpointId;
            _breakpoints[id] = (spec, bp);
            return DescribeNoLock(id, spec, bp);
        }
    }

    /// <summary>Replaces all breakpoints of <paramref name="file"/> with <paramref name="specs"/> (DAP-style).</summary>
    public IReadOnlyList<BreakpointInfo> SetBreakpoints(string file, IReadOnlyList<BreakpointSpec> specs)
    {
        lock (_lock)
        {
            foreach (var (id, entry) in _breakpoints.Where(kv => PathEquals(kv.Value.Spec.File, file)).ToList())
            {
                _store.Remove(entry.Bp);
                _breakpoints.Remove(id);
            }
        }
        return specs.Select(s => SetBreakpoint(s with { File = file })).ToList();
    }

    public bool RemoveBreakpoint(int id)
    {
        lock (_lock)
        {
            if (!_breakpoints.Remove(id, out var entry)) return false;
            _store.Remove(entry.Bp);
            return true;
        }
    }

    public void RemoveAllBreakpoints()
    {
        lock (_lock)
        {
            foreach (var entry in _breakpoints.Values) _store.Remove(entry.Bp);
            _breakpoints.Clear();
        }
    }

    public IReadOnlyList<BreakpointInfo> ListBreakpoints()
    {
        lock (_lock) return _breakpoints.Select(kv => DescribeNoLock(kv.Key, kv.Value.Spec, kv.Value.Bp)).ToList();
    }

    private BreakpointInfo DescribeNoLock(int id, BreakpointSpec spec, Breakpoint bp)
    {
        bool verified = false;
        string? message = null;
        foreach (var e in _processes.Values)
        {
            var s = e.Debugger.Session;
            try
            {
                if (bp.GetStatus(s) == BreakEventStatus.Bound) verified = true;
                message ??= bp.GetStatusMessage(s);
            }
            catch { /* session may be gone */ }
        }
        return new BreakpointInfo(id, spec, verified, string.IsNullOrEmpty(message) ? null : message);
    }

    /// <summary>
    /// Sets exception catchpoints. Unhandled exceptions always stop the debuggee (Mono.Debugging
    /// default); <see cref="ExceptionFilters.BreakOnUnhandled"/>=false is currently ignored.
    /// </summary>
    public void SetExceptionFilters(ExceptionFilters filters)
    {
        lock (_lock)
        {
            _exceptionFilters = filters;
            _store.ClearCatchpoints();
            foreach (var t in filters.FirstChanceTypes ?? [])
                _store.AddCatchpoint(t, includeSubclasses: true);
        }
    }

    public ExceptionFilters GetExceptionFilters() { lock (_lock) return _exceptionFilters; }

    // ------------------------------------------------------------------ inspection

    public IReadOnlyList<ThreadSnapshot> GetThreads(int? pid = null)
    {
        var result = new List<ThreadSnapshot>();
        foreach (var pd in Processes().Where(p => pid is null || p.Pid == pid))
        {
            if (pd.HasExited) continue;
            if (!pd.IsStopped)
            {
                // Thread enumeration needs a suspended VM; report only what we know.
                continue;
            }
            foreach (var t in pd.GetThreads())
                result.Add(new ThreadSnapshot(pd.Pid, t.Id, t.Name ?? "", SafeLocation(t), pd.IsStopped));
        }
        return result;
    }

    private static string? SafeLocation(ThreadInfo t)
    {
        try { return t.Location; } catch { return null; }
    }

    public IReadOnlyList<FrameSnapshot> GetCallStack(int pid, long threadId, int maxFrames = 50)
    {
        var pd = RequireStoppedProcess(pid);
        var bt = pd.GetBacktrace(threadId) ?? throw new ArgumentException($"pid {pid}: no backtrace for thread {threadId}");
        var count = Math.Min(bt.FrameCount, maxFrames);
        var frames = new List<FrameSnapshot>(count);
        for (int i = 0; i < count; i++)
        {
            var f = bt.GetFrame(i);
            var loc = f.SourceLocation;
            frames.Add(new FrameSnapshot(i, loc?.MethodName ?? "?", loc?.FileName, loc?.Line ?? 0, loc?.Column ?? 0, f.IsExternalCode, f.HasDebugInfo));
        }
        return frames;
    }

    public SourceLocationInfo? GetCurrentSourceLocation(int pid, long threadId)
    {
        var pd = RequireStoppedProcess(pid);
        return TryDescribeLocation(pd.GetBacktrace(threadId));
    }

    public IReadOnlyList<VariableSnapshot> GetLocals(int pid, long threadId, int frameIndex = 0)
    {
        var frame = RequireFrame(pid, threadId, frameIndex);
        return frame.GetAllLocals().Select(v => Describe(pid, v)).ToList();
    }

    public VariableSnapshot? GetVariable(int pid, long threadId, int frameIndex, string name)
    {
        var frame = RequireFrame(pid, threadId, frameIndex);
        var v = frame.GetAllLocals().FirstOrDefault(l => l.Name == name);
        return v is null ? null : Describe(pid, v);
    }

    public VariableSnapshot Evaluate(int pid, long threadId, int frameIndex, string expression)
    {
        var frame = RequireFrame(pid, threadId, frameIndex);
        var v = frame.GetExpressionValue(expression, _sessionOptions.EvaluationOptions);
        return Describe(pid, v);
    }

    /// <summary>Children of a previously returned value. Handles are invalidated when the owning process resumes.</summary>
    public IReadOnlyList<VariableSnapshot> ExpandVariable(string expansionHandle, int maxChildren = 100)
    {
        (int Pid, ObjectValue Value) entry;
        lock (_lock)
        {
            if (!_values.TryGetValue(expansionHandle, out entry))
                throw new ArgumentException($"unknown or expired expansion handle '{expansionHandle}'");
        }
        var children = entry.Value.GetAllChildren(_sessionOptions.EvaluationOptions);
        return children.Take(maxChildren).Select(c => Describe(entry.Pid, c)).ToList();
    }

    public IReadOnlyList<AssemblyInfo> GetLoadedAssemblies(int? pid = null)
    {
        var result = new List<AssemblyInfo>();
        foreach (var pd in Processes().Where(p => pid is null || p.Pid == pid))
        {
            if (!pd.IsConnected) continue;
            try
            {
                var vm = pd.Session.VirtualMachine;
                if (vm is null) continue;
                foreach (var asm in vm.RootDomain.GetAssemblies())
                {
                    string? path = null;
                    try { path = asm.Location; } catch { /* not available for dynamic/in-memory */ }
                    result.Add(new AssemblyInfo(pd.Pid, asm.GetName().Name ?? "?", path));
                }
            }
            catch (Exception ex) { _log($"pid {pd.Pid}: assemblies unavailable: {ex.Message}"); }
        }
        return result;
    }

    /// <summary>Details of the exception of the last stop, if it was an exception stop.</summary>
    public (string Type, string Message, string StackTrace)? GetExceptionDetails()
    {
        var last = LastStop;
        if (last is null || last.Reason is not (StopReason.Exception or StopReason.UnhandledException)) return null;
        var pd = RequireStoppedProcess(last.Pid);
        var bt = pd.StopBacktrace;
        if (bt is null || bt.FrameCount == 0) return null;
        var ei = bt.GetFrame(0).GetException();
        if (ei is null) return null;
        var trace = string.Join('\n', (ei.StackTrace ?? []).Select(f => $"  at {f.DisplayText}"));
        return (ei.Type, ei.Message, trace);
    }

    public IReadOnlyList<string> GetAppOutput(int maxLines = 200)
    {
        lock (_lock) return _appOutput.TakeLast(maxLines).ToList();
    }

    public IReadOnlyList<string> GetDebuggerOutput(int maxLines = 200)
    {
        lock (_lock) return _debuggerOutput.TakeLast(maxLines).ToList();
    }

    // ------------------------------------------------------------------ helpers

    private IEnumerable<Engine.ProcessDebugger> Processes()
    {
        lock (_lock) return _processes.Values.Select(e => e.Debugger).ToArray();
    }

    private IEnumerable<Engine.ProcessDebugger> StoppedProcesses() => Processes().Where(p => p.IsStopped);

    private Engine.ProcessDebugger RequireStoppedProcess(int pid)
    {
        Engine.ProcessDebugger? pd;
        lock (_lock) pd = _processes.TryGetValue(pid, out var e) ? e.Debugger : null;
        if (pd is null) throw new ArgumentException($"unknown process {pid}");
        if (!pd.IsStopped) throw new InvalidSessionStateException($"process {pid} is not stopped");
        return pd;
    }

    private StackFrame RequireFrame(int pid, long threadId, int frameIndex)
    {
        var pd = RequireStoppedProcess(pid);
        var bt = pd.GetBacktrace(threadId) ?? throw new ArgumentException($"pid {pid}: no backtrace for thread {threadId}");
        if (frameIndex < 0 || frameIndex >= bt.FrameCount)
            throw new ArgumentOutOfRangeException(nameof(frameIndex), $"frame {frameIndex} out of range (0..{bt.FrameCount - 1})");
        return bt.GetFrame(frameIndex);
    }

    private static SourceLocationInfo? TryDescribeLocation(Backtrace? bt)
    {
        try
        {
            if (bt is null || bt.FrameCount == 0) return null;
            var loc = bt.GetFrame(0).SourceLocation;
            return loc is null ? null : new SourceLocationInfo(loc.FileName, loc.Line, loc.Column, loc.MethodName);
        }
        catch { return null; }
    }

    private VariableSnapshot Describe(int pid, ObjectValue v)
    {
        if (v.IsEvaluating)
            v.WaitHandle.WaitOne(_sessionOptions.EvaluationOptions.EvaluationTimeout + 2000);
        string? handle = null;
        if (v.HasChildren && !v.IsError && !v.IsNull)
        {
            lock (_lock)
            {
                handle = $"{pid}:{++_nextValueId}";
                _values[handle] = (pid, v);
            }
        }
        string value;
        try { value = v.Value ?? "null"; } catch (Exception ex) { value = $"<error: {ex.Message}>"; }
        string display;
        try { display = v.DisplayValue ?? value; } catch { display = value; }
        return new VariableSnapshot(v.Name ?? "", v.TypeName ?? "", value, display, v.HasChildren, handle, v.IsError);
    }

    private void InvalidateValuesNoLock(int pid)
    {
        foreach (var k in _values.Where(kv => kv.Value.Pid == pid).Select(kv => kv.Key).ToList())
            _values.Remove(k);
    }

    private void AppendAppOutput(string line)
    {
        lock (_lock)
        {
            _appOutput.Add(line);
            if (_appOutput.Count > MaxOutputLines) _appOutput.RemoveRange(0, _appOutput.Count - MaxOutputLines);
        }
    }

    private void SetState(SessionState s)
    {
        lock (_lock)
        {
            if (_state == s) return;
            _state = s;
        }
        _log($"state -> {s}");
        Signal();
        StateChanged?.Invoke(this, s);
    }

    private void Signal()
    {
        TaskCompletionSource<bool> old;
        lock (_lock)
        {
            old = _changed;
            _changed = NewSignal();
        }
        old.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static bool PathEquals(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
