using System.Text.RegularExpressions;
using System.Text;
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
    private readonly Dictionary<int, Task> _attaching = new();
    private readonly HashSet<int> _unhandledReported = new();
    private List<ExceptionRule> _exceptionRules = new();
    private List<ExceptionRule> _globalRules = new();
    private IExceptionRuleSource? _globalRuleSource;
    private DateTime? _globalRulesSeenUtc;
    private readonly Dictionary<int, (BreakpointSpec Spec, Breakpoint Bp)> _breakpoints = new();
    private readonly List<AppLogLine> _appOutput = new();
    private readonly List<string> _debuggerOutput = new();
    private readonly Dictionary<string, (int Pid, ObjectValue Value)> _values = new();
    private readonly DebuggerSessionOptions _sessionOptions;

    private TaskCompletionSource<bool> _changed = NewSignal();
    private SessionState _state = SessionState.NotStarted;
    private long _generation;
    private StopEvent? _lastStop;
    private (string Type, string Message, string StackTrace)? _lastExceptionSnapshot;
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
        // Generous by default: the first heavy invocation in a process (culture/ICU init behind
        // DateTime.ToString, debugger type proxies) can take many seconds on an emulator, and an
        // invocation that is ABORTED on timeout leaves the stopped thread unusable for further
        // evaluation. So the timeout must be long enough to let that first invoke finish rather
        // than be aborted.
        _sessionOptions.EvaluationOptions.EvaluationTimeout = 12000;
        _sessionOptions.EvaluationOptions.MemberEvaluationTimeout = 18000;
    }

    /// <summary>
    /// Tunes how values are evaluated in the debuggee for all current and future processes of
    /// this session. Null leaves a setting unchanged. Lower timeouts make inspection snappier
    /// on fast devices; disabling ToString/target invocation avoids wedging the stopped thread
    /// on very slow ones (values then show as type names and raw fields).
    /// </summary>
    public void SetEvaluationOptions(int? evaluationTimeoutMs = null, int? memberEvaluationTimeoutMs = null, bool? allowToStringCalls = null, bool? allowTargetInvoke = null)
    {
        var o = _sessionOptions.EvaluationOptions;
        if (evaluationTimeoutMs is > 0) o.EvaluationTimeout = evaluationTimeoutMs.Value;
        if (memberEvaluationTimeoutMs is > 0) o.MemberEvaluationTimeout = memberEvaluationTimeoutMs.Value;
        if (allowToStringCalls is not null) o.AllowToStringCalls = allowToStringCalls.Value;
        if (allowTargetInvoke is not null) o.AllowTargetInvoke = allowTargetInvoke.Value;
        _log($"evaluation options: timeout={o.EvaluationTimeout}ms member={o.MemberEvaluationTimeout}ms toString={o.AllowToStringCalls} invoke={o.AllowTargetInvoke}");
    }

    public (int EvaluationTimeoutMs, int MemberEvaluationTimeoutMs, bool AllowToStringCalls, bool AllowTargetInvoke) GetEvaluationOptions()
    {
        var o = _sessionOptions.EvaluationOptions;
        return (o.EvaluationTimeout, o.MemberEvaluationTimeout, o.AllowToStringCalls, o.AllowTargetInvoke);
    }

    /// <summary>Raised after every state transition (on the thread that caused it).</summary>
    public event EventHandler<SessionState>? StateChanged;

    /// <summary>Raised for every stop of any debuggee process.</summary>
    public event EventHandler<StopEvent>? Stopped;

    /// <summary>
    /// Raised for every line of debuggee output as it arrives (logcat lines of the app's processes
    /// and anything it wrote to stdout/stderr). Raised on the thread that produced the line.
    /// </summary>
    public event EventHandler<AppLogLine>? AppOutput;

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
        launcher.AppOutput += AppendAppOutput;
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

    /// <summary>
    /// Attaches a debuggee process, deduplicated per pid: the first caller starts the
    /// connection and every other caller (the direct launch path and the AgentDetected
    /// event race) awaits the same in-flight task. The process is exposed in
    /// <see cref="_processes"/> only once the SDB handshake completed, so operations that
    /// enumerate processes (pause, continue) never touch a half-connected VM.
    /// </summary>
    private Task AttachProcessAsync(AgentReady ready, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_state == SessionState.Exited || _processes.ContainsKey(ready.Pid))
                return Task.CompletedTask;
            if (_attaching.TryGetValue(ready.Pid, out var inflight))
                return inflight;
            var task = AttachCoreAsync(ready, ct);
            _attaching[ready.Pid] = task;
            return task;
        }
    }

    private async Task AttachCoreAsync(AgentReady ready, CancellationToken ct)
    {
        var pd = new Engine.ProcessDebugger(ready.Pid, ready.ProcessName ?? _app!.PackageName, ready.Port, _store, _sessionOptions, _log);
        pd.Stopped += OnProcessStopped;
        pd.Resumed += OnProcessResumed;
        pd.Exited += OnProcessExited;
        pd.Output += (p, isErr, text) => AppendAppOutput(
            new AppLogLine(DeviceNow(), p.Pid, 0, isErr ? 'E' : 'I', isErr ? "stderr" : "stdout", text.TrimEnd()));
        try
        {
            await pd.ConnectAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            lock (_lock) _processes[ready.Pid] = new ProcessDebuggerEntry(pd);
        }
        catch
        {
            pd.Dispose();
            throw;
        }
        finally
        {
            lock (_lock) _attaching.Remove(ready.Pid);
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
        // An unhandled exception tears the process down, and the runtime usually raises further
        // unhandled exceptions on other threads while it dies. Only the first one is the failure
        // the caller cares about: report that one and resume the rest automatically, so the app
        // reaches its end and the session settles on Exited instead of asking for N continues.
        if (pd.LastStopReason == StopReason.UnhandledException)
        {
            bool alreadyReported;
            lock (_lock) alreadyReported = !_unhandledReported.Add(pd.Pid);
            if (alreadyReported)
            {
                _log($"pid {pd.Pid}: further unhandled exception while the process is dying - resumed automatically (the first one is kept in the exception details)");
                pd.Continue();
                return;
            }
        }

        // Capture what can be read without calling into the debuggee, while the VM is certainly
        // suspended: for an unhandled exception the runtime may tear the process down right after
        // this event, and a later query would find the VM gone.
        (string Type, string Message, string StackTrace)? snapshot = null;
        if (pd.LastStopReason is StopReason.Exception or StopReason.UnhandledException)
            snapshot = CaptureExceptionAtStop(pd);

        // Rules apply to first-chance exceptions only. An unhandled one always stops: the process
        // is going down either way, and passing it over would leave the caller with an app that
        // vanished for no stated reason.
        if (pd.LastStopReason == StopReason.Exception && HandledByExceptionRules(pd, snapshot))
            return;

        ReportStop(pd, snapshot, snapshot?.Message);
    }

    /// <summary>
    /// Publishes a stop: state, generation, last-stop record, and the events waiters are on. Split
    /// out because the exception rules may decide on a worker thread, after the event thread has
    /// already returned.
    /// </summary>
    private void ReportStop(Engine.ProcessDebugger pd, (string Type, string Message, string StackTrace)? snapshot, string? message)
    {
        StopEvent ev;
        lock (_lock)
        {
            _generation++;
            var loc = TryDescribeLocation(pd.StopBacktrace);
            string? exType = null;
            if (pd.LastStopReason is StopReason.Exception or StopReason.UnhandledException)
            {
                exType = snapshot?.Type;
                _lastExceptionSnapshot = snapshot is null
                    ? null
                    : (snapshot.Value.Type, message ?? snapshot.Value.Message, snapshot.Value.StackTrace);
            }
            else
            {
                _lastExceptionSnapshot = null;
                message = null;
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
        bool allGone, nowRunning = false;
        lock (_lock)
        {
            InvalidateValuesNoLock(pd.Pid);
            allGone = _processes.Values.All(e => e.Debugger.HasExited || e.Died);
            if (allGone && _state != SessionState.Exited && _state != SessionState.NotStarted)
            {
                _generation++;
            }
            else if (!allGone && _state == SessionState.Stopped
                     && !_processes.Values.Any(e => e.Debugger.IsStopped && !e.Debugger.HasExited))
            {
                // The process that was stopped is the one that just died: nothing is suspended
                // any more, so the session is running again rather than still "stopped".
                _state = SessionState.Running;
                nowRunning = true;
            }
        }
        Signal();
        if (nowRunning) StateChanged?.Invoke(this, SessionState.Running);
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
        // Before resuming, not after: the shared rules file is meant to be edited while the app is
        // stopped, and the whole point is that the edit governs what happens next.
        ReloadGlobalRulesIfChanged();
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
        // A step resumes the app too, so the shared rules get the same chance to be re-read.
        ReloadGlobalRulesIfChanged();
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
            ApplyHitCount(bp, spec);
            if (!string.IsNullOrWhiteSpace(spec.LogMessage))
            {
                // A logpoint traces and carries on: HitAction without the Break flag is what tells
                // Mono not to suspend. The `{expression}` parts are substituted by the runtime.
                bp.TraceExpression = spec.LogMessage;
                bp.HitAction = HitAction.PrintExpression;
            }
            var id = ++_nextBreakpointId;
            _breakpoints[id] = (spec, bp);
            return DescribeNoLock(id, spec, bp);
        }
    }

    /// <summary>
    /// Applies whichever of the two hit-count forms was given. `HitCondition` is the richer one and
    /// wins; `HitCount` stays for callers that only ever wanted "from the Nth hit".
    /// </summary>
    private static void ApplyHitCount(Breakpoint bp, BreakpointSpec spec)
    {
        var condition = spec.HitCondition?.Trim();
        if (string.IsNullOrEmpty(condition))
        {
            if (spec.HitCount > 0)
            {
                bp.HitCountMode = HitCountMode.GreaterThanOrEqualTo;
                bp.HitCount = spec.HitCount;
            }
            return;
        }

        var (mode, digits) = condition switch
        {
            ['>', '=', .. var rest] => (HitCountMode.GreaterThanOrEqualTo, rest),
            ['<', '=', .. var rest] => (HitCountMode.LessThanOrEqualTo, rest),
            ['>', .. var rest] => (HitCountMode.GreaterThan, rest),
            ['<', .. var rest] => (HitCountMode.LessThan, rest),
            ['=', .. var rest] => (HitCountMode.EqualTo, rest),
            ['%', .. var rest] => (HitCountMode.MultipleOf, rest),
            _ => (HitCountMode.GreaterThanOrEqualTo, condition),
        };
        if (!int.TryParse(digits.Trim(), out var count) || count <= 0)
            throw new ArgumentException(
                $"'{spec.HitCondition}' is not a hit condition. Use 5 or >=5 (from the fifth hit), " +
                $">5 (after it), =5 (only it), %5 (every fifth).", nameof(spec));
        bp.HitCountMode = mode;
        bp.HitCount = count;
    }

    /// <summary>
    /// Sets a breakpoint and gives Mono a short window to bind it before reporting the result.
    /// Binding is asynchronous: a breakpoint on a type the runtime has not loaded yet is reported
    /// unbound (with a status message that reads like a failure) for a few milliseconds and then
    /// verifies on its own. Waiting removes that misleading answer. Returns as soon as the
    /// breakpoint is bound, immediately if no process is attached yet (nothing can bind it).
    /// </summary>
    /// <param name="spec">The breakpoint to add.</param>
    /// <param name="settle">How long to wait for binding before answering anyway.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<BreakpointInfo> SetBreakpointAsync(BreakpointSpec spec, TimeSpan settle, CancellationToken ct)
    {
        var info = SetBreakpoint(spec);
        lock (_lock)
            if (_processes.Count == 0) return info;

        var deadline = DateTime.UtcNow + settle;
        while (!info.Verified && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25, ct).ConfigureAwait(false);
            lock (_lock)
            {
                if (!_breakpoints.TryGetValue(info.Id, out var entry)) return info;
                info = DescribeNoLock(info.Id, entry.Spec, entry.Bp);
            }
        }
        return info;
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

    /// <summary>
    /// Replaces all breakpoints of <paramref name="file"/> and, like
    /// <see cref="SetBreakpointAsync"/>, gives the runtime a short window to bind them before
    /// reporting.
    /// </summary>
    /// <param name="file">Source file whose breakpoints are replaced.</param>
    /// <param name="specs">The breakpoints the file should end up with (empty clears it).</param>
    /// <param name="settle">How long to wait for binding before answering anyway.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<BreakpointInfo>> SetBreakpointsAsync(
        string file, IReadOnlyList<BreakpointSpec> specs, TimeSpan settle, CancellationToken ct)
    {
        var infos = SetBreakpoints(file, specs);
        lock (_lock)
            if (_processes.Count == 0 || infos.Count == 0) return infos;

        var deadline = DateTime.UtcNow + settle;
        while (infos.Any(i => !i.Verified) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25, ct).ConfigureAwait(false);
            lock (_lock)
                infos = infos
                    .Select(i => _breakpoints.TryGetValue(i.Id, out var e) ? DescribeNoLock(i.Id, e.Spec, e.Bp) : i)
                    .ToList();
        }
        return infos;
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
        // The status message belongs to whichever process answered first, and in a multi-process
        // app that can be one where the assembly is not loaded. Once any process has the
        // breakpoint bound, "will not currently be hit" is misleading: drop it.
        if (verified) message = null;
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

    /// <summary>
    /// Replaces the exception rules. They are consulted for first-chance exceptions only: an
    /// unhandled one always stops, because the process is going down either way and saying nothing
    /// would leave the caller with an app that simply vanished.
    /// </summary>
    /// <param name="rules">Ordered; the first whose criteria all match decides. Empty restores
    /// plain filter behaviour.</param>
    public void SetExceptionRules(IReadOnlyList<ExceptionRule> rules)
    {
        foreach (var rule in rules)
        {
            // Compile now, so a bad pattern is the caller's error rather than a surprise on the
            // first exception - by which time the app is running and the failure is far away.
            if (rule.MessageRegex is { Length: > 0 } pattern)
            {
                try { _ = new Regex(pattern); }
                catch (ArgumentException ex)
                {
                    throw new ArgumentException($"'{pattern}' is not a valid regular expression: {ex.Message}", nameof(rules));
                }
            }
        }
        lock (_lock) _exceptionRules = rules.ToList();
        _log($"exception rules: {rules.Count} in force");
    }

    /// <summary>The exception rules currently in force, in order.</summary>
    public IReadOnlyList<ExceptionRule> GetExceptionRules() { lock (_lock) return _exceptionRules; }

    /// <summary>
    /// Sets the shared rule source consulted after the session's own rules. Project rules win,
    /// because the general baseline lives in the shared file and the specific case in the session.
    /// The source is re-read on resume whenever it has changed, so a rule can be edited while the
    /// app is stopped and take effect on the next continue.
    /// </summary>
    /// <param name="source">Null removes the shared source.</param>
    public void SetGlobalExceptionRuleSource(IExceptionRuleSource? source)
    {
        lock (_lock)
        {
            _globalRuleSource = source;
            _globalRulesSeenUtc = null;
            _globalRules = [];
        }
        if (source is null)
        {
            _log("no shared exception rules");
            return;
        }
        ReloadGlobalRulesIfChanged(force: true);
    }

    /// <summary>
    /// Re-reads the shared rules when the source has changed since they were last read. Called
    /// before every resume: the point is to let a rule be edited while the app is stopped.
    /// </summary>
    private void ReloadGlobalRulesIfChanged(bool force = false)
    {
        IExceptionRuleSource? source;
        DateTime? seen;
        lock (_lock) { source = _globalRuleSource; seen = _globalRulesSeenUtc; }
        if (source is null) return;

        DateTime? changed;
        try { changed = source.LastChangedUtc; }
        catch (Exception ex) { _log($"could not check {source.Description}: {ex.Message}"); return; }

        if (!force && changed == seen) return;

        IReadOnlyList<ExceptionRule> rules;
        try { rules = source.Load(); }
        catch (Exception ex)
        {
            // A half-written file while the user is editing is the normal case here, not an error
            // worth failing a resume over. Keep what we had and say so.
            _log($"could not read {source.Description}: {ex.Message} - the rules already in force are kept");
            return;
        }

        lock (_lock) { _globalRules = rules.ToList(); _globalRulesSeenUtc = changed; }
        _log($"shared exception rules from {source.Description}: {rules.Count} in force");
    }

    /// <summary>Session rules first, then the shared ones. First match still wins overall.</summary>
    private List<ExceptionRule> EffectiveExceptionRulesNoLock()
        => _globalRules.Count == 0 ? _exceptionRules : [.. _exceptionRules, .. _globalRules];

    /// <summary>
    /// Decides what to do with a first-chance exception. Returns null when no rule matches, which
    /// leaves the filters in charge.
    /// </summary>
    private ExceptionAction? MatchExceptionRules(string type, string? message, string? sourceFile)
    {
        List<ExceptionRule> rules;
        lock (_lock) rules = EffectiveExceptionRulesNoLock();
        foreach (var rule in rules)
        {
            if (rule.Type is { Length: > 0 } exact && !string.Equals(type, exact, StringComparison.Ordinal)) continue;
            if (rule.TypeContains is { Length: > 0 } part && !type.Contains(part, StringComparison.Ordinal)) continue;
            if (rule.SourceFileContains is { Length: > 0 } file
                && (sourceFile is null || !sourceFile.Contains(file, StringComparison.OrdinalIgnoreCase))) continue;
            if (rule.MessageContains is { Length: > 0 } text
                && (message is null || !message.Contains(text, StringComparison.OrdinalIgnoreCase))) continue;
            if (rule.MessageRegex is { Length: > 0 } pattern
                && (message is null || !Regex.IsMatch(message, pattern))) continue;
            return rule.Action;
        }
        return null;
    }

    /// <summary>True when some rule can only be decided once the message is known.</summary>
    private bool RulesNeedMessage()
    {
        lock (_lock)
            return EffectiveExceptionRulesNoLock().Any(r => r.MessageContains is { Length: > 0 } || r.MessageRegex is { Length: > 0 });
    }

    /// <summary>
    /// Applies the rules to a first-chance exception stop. Returns true when the stop was handled
    /// (logged and/or resumed) and must not be reported.
    /// </summary>
    private bool HandledByExceptionRules(Engine.ProcessDebugger pd, (string Type, string Message, string StackTrace)? snapshot)
    {
        lock (_lock) if (EffectiveExceptionRulesNoLock().Count == 0) return false;

        var type = snapshot?.Type ?? "";
        var site = TryDescribeLocation(pd.StopBacktrace)?.File;

        // The message needs a call into the debuggee, which is not allowed on this thread - it is
        // the event thread and the VM is not "suspended" from its point of view. When no rule asks
        // for the message the decision is made here; when one does, it moves to a worker.
        if (!RulesNeedMessage())
        {
            var decided = MatchExceptionRules(type, null, site);
            return decided is not null && ActOnRule(pd, decided.Value, type, null, snapshot);
        }

        var pid = pd.Pid;
        var threadId = pd.StopThread?.Id ?? 0;
        Task.Run(() =>
        {
            string? message = null;
            try { message = ResolveExceptionMessageLive(pid, threadId); }
            catch (Exception ex) { _log($"pid {pid}: reading the exception message for the rules failed: {ex.Message}"); }

            var decided = MatchExceptionRules(type, message, site);
            if (decided is not null && decided.Value != ExceptionAction.Break)
            {
                ActOnRule(pd, decided.Value, type, message, snapshot);
                return;
            }
            // No rule, or one that says break: report it as an ordinary exception stop.
            ReportStop(pd, snapshot, message);
        });
        return true;
    }

    /// <summary>Carries out a rule's action. Returns true when the app was resumed.</summary>
    private bool ActOnRule(Engine.ProcessDebugger pd, ExceptionAction action, string type, string? message, (string Type, string Message, string StackTrace)? snapshot)
    {
        if (action == ExceptionAction.Break) return false;

        var described = string.IsNullOrEmpty(message) ? type : $"{type}: {message}";
        switch (action)
        {
            case ExceptionAction.Log:
                _log($"[pid {pd.Pid}] exception rule: {described} (not stopping)");
                break;
            case ExceptionAction.LogStack:
                _log($"[pid {pd.Pid}] exception rule: {described} (not stopping)\n{snapshot?.StackTrace}");
                break;
            case ExceptionAction.Ignore:
            default:
                break;
        }
        try { pd.Continue(); }
        catch (Exception ex) { _log($"pid {pd.Pid}: resuming after an exception rule failed: {ex.Message}"); }
        return true;
    }

    // ------------------------------------------------------------------ inspection

    public IReadOnlyList<ThreadSnapshot> GetThreads(int? pid = null)
        => RunBounded("GetThreads", () => GetThreadsCore(pid));

    private IReadOnlyList<ThreadSnapshot> GetThreadsCore(int? pid)
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
                result.Add(new ThreadSnapshot(pd.Pid, t.Id, ThreadDisplayName(t), SafeLocation(t), pd.IsStopped));
        }
        return result;
    }

    /// <summary>
    /// Mono reports no name for the main thread (managed id 1) and for unnamed threads;
    /// give them a stable label so frontends and models can tell them apart.
    /// </summary>
    private static string ThreadDisplayName(ThreadInfo t)
    {
        if (!string.IsNullOrEmpty(t.Name)) return t.Name;
        return t.Id == 1 ? "Main" : $"Thread {t.Id}";
    }

    private static string? SafeLocation(ThreadInfo t)
    {
        try { return t.Location; } catch { return null; }
    }

    public IReadOnlyList<FrameSnapshot> GetCallStack(int pid, long threadId, int maxFrames = 50)
    {
        return RunBounded("GetCallStack", () => GetCallStackCore(pid, threadId, maxFrames));
    }

    private IReadOnlyList<FrameSnapshot> GetCallStackCore(int pid, long threadId, int maxFrames)
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
        return RunBounded("GetLocals", () => WithBreakpointsDisarmed(() =>
        {
            var frame = RequireFrame(pid, threadId, frameIndex);
            return (IReadOnlyList<VariableSnapshot>)frame.GetAllLocals().Select(v => Describe(pid, v)).ToList();
        }));
    }

    public VariableSnapshot? GetVariable(int pid, long threadId, int frameIndex, string name)
    {
        return RunBounded("GetVariable", () => WithBreakpointsDisarmed(() =>
        {
            var frame = RequireFrame(pid, threadId, frameIndex);
            var v = frame.GetAllLocals().FirstOrDefault(l => l.Name == name);
            return v is null ? null : Describe(pid, v);
        }));
    }

    public VariableSnapshot Evaluate(int pid, long threadId, int frameIndex, string expression)
    {
        return RunBounded("Evaluate", () => WithBreakpointsDisarmed(() => EvaluateCore(pid, threadId, frameIndex, expression)));
    }

    private VariableSnapshot EvaluateCore(int pid, long threadId, int frameIndex, string expression)
    {
        var frame = RequireFrame(pid, threadId, frameIndex);
        // Reject syntactically invalid expressions up front, without evaluating.
        var validation = frame.ValidateExpression(expression, _sessionOptions.EvaluationOptions);
        if (!validation.IsValid)
        {
            var msg = string.IsNullOrEmpty(validation.Message) ? "invalid expression" : validation.Message;
            return new VariableSnapshot(expression, "", msg, msg, false, null, IsError: true);
        }
        try
        {
            var v = frame.GetExpressionValue(expression, _sessionOptions.EvaluationOptions);
            if (v.IsEvaluating) v.WaitHandle.WaitOne(_sessionOptions.EvaluationOptions.EvaluationTimeout + 2000);
            if (!IsConcreteValue(v))
            {
                string reason;
                try { reason = string.IsNullOrEmpty(v.Value) ? "could not evaluate expression" : v.Value; }
                catch { reason = "could not evaluate expression"; }
                return new VariableSnapshot(expression, v.TypeName ?? "", reason, reason, false, null, IsError: true);
            }
            return Describe(pid, v);
        }
        catch (Exception ex)
        {
            // The Mono evaluator can also throw (e.g. NotSupportedException when an identifier is
            // taken for a namespace) instead of returning an error value; surface it as one.
            return new VariableSnapshot(expression, "", ex.Message, ex.Message, false, null, IsError: true);
        }
    }

    /// <summary>
    /// True only when the evaluated value is a real object/array/primitive/null. Unknown
    /// identifiers, namespaces, types and (implicit-)not-supported results are not values —
    /// Mono sometimes returns them without the Error flag, so check the kind explicitly.
    /// </summary>
    private static bool IsConcreteValue(Mono.Debugging.Client.ObjectValue v)
    {
        if (v.IsError || v.IsUnknown || v.IsNotSupported || v.IsImplicitNotSupported) return false;
        if (v.IsNull) return true;
        var kind = v.Flags & Mono.Debugging.Client.ObjectValueFlags.KindMask;
        return kind is Mono.Debugging.Client.ObjectValueFlags.Object
            or Mono.Debugging.Client.ObjectValueFlags.Array
            or Mono.Debugging.Client.ObjectValueFlags.Primitive;
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
        return RunBounded("ExpandVariable", () => WithBreakpointsDisarmed(() =>
        {
            var children = entry.Value.GetAllChildren(_sessionOptions.EvaluationOptions);
            return (IReadOnlyList<VariableSnapshot>)children.Take(maxChildren).Select(c => Describe(entry.Pid, c)).ToList();
        }));
    }


    /// <summary>
    /// Source files the debuggee's runtime knows under <paramref name="fileName"/>, with the exact
    /// paths compiled into the PDB. Answers the question a pending breakpoint raises — "is my path
    /// the path the app was built with?" — without guessing. Matching is by file name, so both
    /// <c>MainActivity.cs</c> and a full path work as input. Does not require a stopped process.
    /// </summary>
    /// <param name="fileName">File name or path; only the file name part is matched.</param>
    /// <param name="pid">Restrict to one process; all attached processes otherwise.</param>
    /// <param name="maxTypesPerFile">Cap on the type names reported per file.</param>
    public IReadOnlyList<SourceFileInfo> GetSourceFiles(string fileName, int? pid = null, int maxTypesPerFile = 20)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("a file name is required", nameof(fileName));

        var name = System.IO.Path.GetFileName(fileName);
        var result = new List<SourceFileInfo>();
        foreach (var pd in Processes().Where(p => pid is null || p.Pid == pid))
        {
            if (!pd.IsConnected) continue;
            var vm = pd.Session.VirtualMachine;
            if (vm is null) continue;

            // path (as the runtime has it) -> types compiled from it
            var byPath = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var type in vm.GetTypesForSourceFile(name, ignoreCase: true))
                {
                    string[] paths;
                    try { paths = type.GetSourceFiles(returnFullPaths: true); }
                    catch (Exception ex) { _log($"source files of {type.FullName}: {ex.Message}"); continue; }

                    foreach (var p in paths)
                    {
                        if (!string.Equals(System.IO.Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!byPath.TryGetValue(p, out var types)) byPath[p] = types = new List<string>();
                        if (types.Count < maxTypesPerFile) types.Add(type.FullName);
                    }
                }
            }
            catch (Exception ex)
            {
                // A process that is mid-handshake or gone answers nothing; the others still can.
                _log($"pid {pd.Pid}: could not look up source file '{name}': {ex.Message}");
                continue;
            }

            foreach (var (path, types) in byPath.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                result.Add(new SourceFileInfo(pd.Pid, path, types));
        }
        return result;
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
    /// <summary>
    /// Details of the exception of the last stop. Captured on the event thread the moment the
    /// process stopped (see <see cref="CaptureExceptionAtStop"/>), because an unhandled exception
    /// tears the debuggee down immediately afterwards and a later live query would find the VM
    /// gone (<c>VMNotSuspendedException</c>).
    /// </summary>
    public (string Type, string Message, string StackTrace)? GetExceptionDetails()
    {
        (string Type, string Message, string StackTrace)? snap;
        StopEvent? last;
        lock (_lock) { snap = _lastExceptionSnapshot; last = _lastStop; }
        if (snap is null) return null;

        // Neither the type nor the message can always be read on the event thread (evaluation is
        // not allowed there, and the stopping frame may have no debug info). Resolve what is
        // missing now, on this thread, while the process is still stopped.
        if (last is not null && (string.IsNullOrEmpty(snap.Value.Message) || string.IsNullOrEmpty(snap.Value.Type)))
        {
            try
            {
                var (type, message) = RunBounded("GetExceptionDetails", () =>
                (
                    string.IsNullOrEmpty(snap.Value.Type) ? ResolveExceptionTypeLive(last.Pid, last.ThreadId) : snap.Value.Type,
                    string.IsNullOrEmpty(snap.Value.Message) ? ResolveExceptionMessageLive(last.Pid, last.ThreadId) : snap.Value.Message
                ));
                if (!string.IsNullOrEmpty(type) || !string.IsNullOrEmpty(message))
                {
                    snap = (type, message, snap.Value.StackTrace);
                    lock (_lock) if (_lastExceptionSnapshot is not null) _lastExceptionSnapshot = snap;
                }
            }
            catch (Exception ex) { _log($"resolving exception details failed: {ex.Message}"); }
        }
        return snap;
    }

    /// <summary>
    /// Captures what can be read from the stop event thread without any debuggee
    /// invocation — the exception type (mirror type name) and the stack trace (from the already
    /// materialized backtrace). Expression/property evaluation is NOT allowed from inside the stop
    /// event handler ("vm is not suspended"), so the message is left empty and resolved later by
    /// <see cref="GetExceptionDetails"/>. Each read is guarded so a failure never loses the rest.
    /// </summary>
    private (string Type, string Message, string StackTrace)? CaptureExceptionAtStop(Engine.ProcessDebugger pd)
    {
        var bt = pd.StopBacktrace;
        if (bt is null || bt.FrameCount == 0) return null;

        var trace = new StringBuilder();
        try
        {
            var count = Math.Min(bt.FrameCount, 50);
            for (int i = 0; i < count; i++)
            {
                var loc = bt.GetFrame(i).SourceLocation;
                trace.Append("  at ").Append(loc?.MethodName ?? "?");
                if (!string.IsNullOrEmpty(loc?.FileName)) trace.Append(" in ").Append(loc.FileName).Append(':').Append(loc.Line);
                trace.Append('\n');
            }
        }
        catch (Exception ex) { _log($"capturing exception stack trace failed: {ex.Message}"); }

        string type = "";
        try { type = bt.GetFrame(0).GetException()?.Type ?? ""; }
        catch (Exception ex) { _log($"capturing exception type failed: {ex.Message}"); }

        return (type, "", trace.ToString().TrimEnd());
    }

    /// <summary>
    /// Reads the exception's type from the live frame. The type captured at stop time comes from
    /// <c>GetException()</c> on the stopping frame, which returns nothing when the throw site is in
    /// an assembly without debug info (MQTTnet in the reference application, for instance). The `$exception` value's
    /// own type name is there either way, and reading it invokes nothing in the debuggee.
    /// </summary>
    private string ResolveExceptionTypeLive(int pid, long threadId)
    {
        Engine.ProcessDebugger? pd;
        lock (_lock) pd = _processes.TryGetValue(pid, out var e) ? e.Debugger : null;
        if (pd is null || !pd.IsStopped) return "";
        var bt = pd.GetBacktrace(threadId);
        if (bt is null || bt.FrameCount == 0) return "";
        var tag = _sessionOptions.EvaluationOptions.CurrentExceptionTag ?? "$exception";
        try
        {
            var v = bt.GetFrame(0).GetExpressionValue(tag, _sessionOptions.EvaluationOptions);
            if (v.IsEvaluating) v.WaitHandle.WaitOne(_sessionOptions.EvaluationOptions.EvaluationTimeout + 2000);
            if (!v.IsEvaluating && !v.IsError) return v.TypeName ?? "";
        }
        catch (Exception ex) { _log($"exception type via {tag}: {ex.Message}"); }
        return "";
    }

    /// <summary>Resolves the exception message via a field/property read on the current thread (VM properly suspended here).</summary>
    private string ResolveExceptionMessageLive(int pid, long threadId)
    {
        Engine.ProcessDebugger? pd;
        lock (_lock) pd = _processes.TryGetValue(pid, out var e) ? e.Debugger : null;
        if (pd is null || !pd.IsStopped) return "";
        var bt = pd.GetBacktrace(threadId);
        if (bt is null || bt.FrameCount == 0) return "";
        var frame = bt.GetFrame(0);
        var tag = _sessionOptions.EvaluationOptions.CurrentExceptionTag ?? "$exception";
        foreach (var expr in new[] { tag + "._message", tag + ".Message" })
        {
            try
            {
                var v = frame.GetExpressionValue(expr, _sessionOptions.EvaluationOptions);
                if (v.IsEvaluating) v.WaitHandle.WaitOne(_sessionOptions.EvaluationOptions.EvaluationTimeout + 2000);
                if (!v.IsEvaluating && IsConcreteValue(v) && !v.IsNull)
                {
                    var s = v.Value ?? "";
                    var unquoted = s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;
                    if (unquoted.Length > 0) return unquoted;
                }
            }
            catch (Exception ex) { _log($"exception message via {expr}: {ex.Message}"); }
        }
        return "";
    }

    /// <summary>
    /// Recent debuggee output (logcat lines of the app's processes plus its stdout/stderr),
    /// newest last. Filters are applied before <paramref name="maxLines"/>, so narrowing the
    /// filter shows older matches rather than fewer.
    /// </summary>
    /// <param name="minLevel">Lowest Android priority to include (V, D, I, W, E, F).</param>
    /// <param name="tagContains">Case-insensitive substring the tag must contain.</param>
    /// <param name="contains">Case-insensitive substring the message must contain.</param>
    /// <param name="pid">Restrict to one process.</param>
    public IReadOnlyList<AppLogLine> GetAppOutput(int maxLines = 200, char? minLevel = null, string? tagContains = null, string? contains = null, int? pid = null)
    {
        const string Priorities = "VDIWEF";
        var floor = minLevel is null ? -1 : Priorities.IndexOf(char.ToUpperInvariant(minLevel.Value));
        lock (_lock)
        {
            IEnumerable<AppLogLine> lines = _appOutput;
            if (floor > 0) lines = lines.Where(l => Priorities.IndexOf(char.ToUpperInvariant(l.Level)) >= floor);
            if (pid is not null) lines = lines.Where(l => l.Pid == pid);
            if (!string.IsNullOrEmpty(tagContains)) lines = lines.Where(l => l.Tag.Contains(tagContains, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(contains)) lines = lines.Where(l => l.Message.Contains(contains, StringComparison.OrdinalIgnoreCase));
            return lines.TakeLast(maxLines).ToList();
        }
    }

    public IReadOnlyList<string> GetDebuggerOutput(int maxLines = 200)
    {
        lock (_lock) return _debuggerOutput.TakeLast(maxLines).ToList();
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Upper bound for synchronous inspection calls into Mono.Debugging. Method invocation in
    /// the debuggee (property getters, ToString) can wedge past its own EvaluationTimeout when
    /// the abort fails (seen: "Aborting invocation of ... DateTime:ToString()" followed by a
    /// never-returning call). A stuck evaluation then leaks one thread-pool thread, but the
    /// session, the caller and the test host survive instead of hanging forever.
    /// </summary>
    private static readonly TimeSpan InspectionTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Runs an operation that invokes code in the debuggee with every breakpoint disarmed.
    /// Mono resumes *all* threads for the duration of an invocation (it only disables
    /// breakpoints on the invoking thread), so a breakpoint that another thread hits meanwhile
    /// suspends the VM with the invocation still in flight: it never returns, gets aborted on
    /// timeout, and the abort can take the process down. Disarming for the duration costs two
    /// round-trips per breakpoint and removes the whole class of failure. Hits that would have
    /// happened during the evaluation are lost by design — an evaluation is not a resume.
    /// </summary>
    private T WithBreakpointsDisarmed<T>(Func<T> f)
    {
        List<BreakEvent> disarmed = new();
        lock (_lock)
        {
            foreach (var be in (IEnumerable<BreakEvent>)_store)
            {
                if (!be.Enabled) continue;
                try { be.Enabled = false; disarmed.Add(be); }
                catch (Exception ex) { _log($"could not disarm a breakpoint for the evaluation: {ex.Message}"); }
            }
        }
        try
        {
            return f();
        }
        finally
        {
            foreach (var be in disarmed)
            {
                try { be.Enabled = true; }
                catch (Exception ex) { _log($"could not re-arm a breakpoint after the evaluation: {ex.Message}"); }
            }
        }
    }

    private T RunBounded<T>(string what, Func<T> f)
    {
        var task = Task.Run(f);
        if (task.Wait(InspectionTimeout))
            return task.GetAwaiter().GetResult();
        _log($"{what}: debuggee evaluation stuck, gave up after {InspectionTimeout.TotalSeconds:F0}s");
        throw new TimeoutException($"{what} did not complete within {InspectionTimeout.TotalSeconds:F0}s: a method invocation in the debuggee appears to be stuck");
    }

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

    /// <summary>
    /// Where a stop happened, as a caller wants to see it: the topmost frame, unless that frame
    /// has no source — a breakpoint on a line that calls into external code can be delivered with
    /// the callee on top (e.g. `Java.Interop.JniPeerMembers.get_StaticMethods` for a line calling
    /// `Android.Util.Log.Debug`). In that case report the nearest frame below that does have
    /// source, so the location is always the user's line when there is one.
    /// </summary>
    private static SourceLocationInfo? TryDescribeLocation(Backtrace? bt)
    {
        try
        {
            if (bt is null || bt.FrameCount == 0) return null;
            var top = bt.GetFrame(0).SourceLocation;
            if (!string.IsNullOrEmpty(top?.FileName))
                return new SourceLocationInfo(top.FileName, top.Line, top.Column, top.MethodName);

            var limit = Math.Min(bt.FrameCount, 20);
            for (int i = 1; i < limit; i++)
            {
                var loc = bt.GetFrame(i).SourceLocation;
                if (!string.IsNullOrEmpty(loc?.FileName))
                    return new SourceLocationInfo(loc.FileName, loc.Line, loc.Column, loc.MethodName);
            }
            // No frame carries source: report the top frame as-is (pause inside runtime code).
            return top is null ? null : new SourceLocationInfo(top.FileName, top.Line, top.Column, top.MethodName);
        }
        catch { return null; }
    }

    private VariableSnapshot Describe(int pid, ObjectValue v)
    {
        if (v.IsEvaluating)
            v.WaitHandle.WaitOne(_sessionOptions.EvaluationOptions.EvaluationTimeout + 2000);
        if (v.IsEvaluating)
        {
            // Still not resolved: report it as such instead of a fake null with an expansion handle.
            const string pending = "<evaluation timed out>";
            return new VariableSnapshot(v.Name ?? "", v.TypeName ?? "", pending, pending, false, null, IsError: true);
        }
        string value;
        try { value = v.Value ?? "null"; } catch (Exception ex) { value = $"<error: {ex.Message}>"; }
        string display;
        try { display = v.DisplayValue ?? value; } catch { display = value; }

        // `IsNull` is only set on the values Mono builds through CreateNullObject. A null a frame
        // reports by another route - the locals of an async state machine that the method has not
        // assigned yet are the common case - arrives with IsNull false and "(null)" as its value,
        // and claims to have children. Expanding it yields nothing, so trust the rendering too.
        var isNull = v.IsNull || string.Equals(value, "(null)", StringComparison.Ordinal);

        string? handle = null;
        if (v.HasChildren && !v.IsError && !isNull)
        {
            lock (_lock)
            {
                handle = $"{pid}:{++_nextValueId}";
                _values[handle] = (pid, v);
            }
        }
        // Mono exposes the elements of an IEnumerable as an extra group child named "IEnumerator",
        // sitting among the iterator's own fields and carrying no value of its own. Say what it is,
        // otherwise the elements look absent and the state machine looks like the whole story.
        if ((v.Flags & Mono.Debugging.Client.ObjectValueFlags.IEnumerable) != 0 && string.IsNullOrEmpty(value))
            value = display = "<enumerated elements: expand>";
        return new VariableSnapshot(v.Name ?? "", v.TypeName ?? "", value, display, handle is not null, handle, v.IsError);
    }

    private void InvalidateValuesNoLock(int pid)
    {
        foreach (var k in _values.Where(kv => kv.Value.Pid == pid).Select(kv => kv.Key).ToList())
            _values.Remove(k);
    }

    /// <summary>
    /// Now, on the device's wall clock — the clock every timestamp in the app output uses.
    /// Falls back to the host clock before the first launch.
    /// </summary>
    private DateTime DeviceNow() => _launcher?.DeviceNow ?? DateTime.Now;

    private void AppendAppOutput(AppLogLine line)
    {
        lock (_lock)
        {
            _appOutput.Add(line);
            if (_appOutput.Count > MaxOutputLines) _appOutput.RemoveRange(0, _appOutput.Count - MaxOutputLines);
        }
        // Outside the lock: a frontend handler must never be able to deadlock the engine.
        try { AppOutput?.Invoke(this, line); }
        catch (Exception ex) { _log($"an app-output subscriber threw: {ex.Message}"); }
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
