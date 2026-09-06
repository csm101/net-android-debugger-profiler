using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NetAndroidDebugger.Core.Launch;

/// <summary>A debuggee process whose Mono soft-debugger agent is listening and waiting for a connection.</summary>
public sealed record AgentReady(int Pid, int Port, string? ProcessName);

/// <summary>
/// Owns the device-side attach dance for one application: deploy (optional), write
/// <c>debug.mono.extra</c>, forward ports, start the activity, watch logcat for every
/// process of the package that initializes its debugger agent, and rotate the property
/// to the next port each time so that helper processes never collide on a port.
/// See ANDROID_ATTACH_NOTES.md ("Multi-process apps") for the verified protocol.
/// </summary>
public sealed class AndroidLauncher : IAsyncDisposable
{
    private const string DebugProperty = DeviceGlobals.DebugMonoExtra;

    // threadtime: "08-20 09:10:39.891  6954  6954 W monodroid-debug: Trying to initialize ..."
    private static readonly Regex LogcatLine = new(
        @"^(?<stamp>\d\d-\d\d\s+\d\d:\d\d:\d\d\.\d+)\s+(?<pid>\d+)\s+(?<tid>\d+)\s+(?<lvl>[VDIWEFS])\s+(?<tag>.*?)\s*:\s(?<msg>.*)$",
        RegexOptions.Compiled);
    private static readonly Regex AgentInit = new(@"Trying to initialize the debugger with options:.*address=127\.0\.0\.1:(?<port>\d+)", RegexOptions.Compiled);
    private static readonly Regex StartProc = new(@"Start proc (?<pid>\d+):(?<name>\S+?)/(?<uid>\S+)", RegexOptions.Compiled);
    private static readonly Regex AmUid = new(@"^u(?<user>\d+)a(?<app>\d+)$", RegexOptions.Compiled);

    private readonly AdbClient _adb;
    private readonly AppTarget _app;
    private readonly LaunchOptions _options;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly Dictionary<int, string> _processNames = new();
    private readonly HashSet<int> _appPids = new();
    private readonly List<int> _forwardedPorts = new();
    private readonly TaskCompletionSource<AgentReady> _firstAgent = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CancellationTokenSource? _logcatCts;
    private Task? _logcatTask;
    private long _deadline;
    private int _nextPort;
    private readonly DevicePropertyOverride _property;
    private volatile bool _shuttingDown;
    private int? _packageUid;
    private readonly SemaphoreSlim _propertyGate = new(1, 1);
    private CancellationTokenSource? _renewCts;
    private Task? _renewTask;

    /// <summary>
    /// Device wall clock minus host wall clock, measured once at launch. logcat stamps its lines
    /// with the device's local time, so every timestamp the session reports is expressed on that
    /// clock; output that reaches us through the debugger is shifted by this offset to match.
    /// Zero until the first launch.
    /// </summary>
    public TimeSpan DeviceClockOffset { get; private set; }

    /// <summary>Device wall clock now, on the same clock logcat uses.</summary>
    public DateTime DeviceNow => DateTime.Now + DeviceClockOffset;

    public AndroidLauncher(AdbClient adb, AppTarget app, LaunchOptions options, Action<string> log)
    {
        _adb = adb;
        _app = app;
        _options = options;
        _log = log;
        _nextPort = options.BaseSdbPort;
        _property = new DevicePropertyOverride(adb, options.DeviceSerial, DebugProperty);
    }

    /// <summary>Raised (from the logcat reader thread) for every process of the package whose agent is listening. The property has already been rotated.</summary>
    public event Action<AgentReady>? AgentDetected;

    /// <summary>Raised for every logcat line that belongs to a known process of the package.</summary>
    public event Action<AppLogLine>? AppOutput;

    /// <summary>Raised when a known process of the package died according to ActivityManager.</summary>
    public event Action<int>? ProcessDied;

    public string DeviceSerial => _options.DeviceSerial;

    /// <summary>Pids known to belong to the package (from ActivityManager and agent detection).</summary>
    public IReadOnlyCollection<int> KnownPids { get { lock (_gate) return _appPids.ToArray(); } }

    /// <summary>Runs <c>dotnet build &lt;project&gt; -t:Install</c> for the app project against the selected device.</summary>
    public async Task DeployAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_app.ProjectPath))
            throw new LaunchException("Deploy requested but AppTarget.ProjectPath is not set");
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "build", _app.ProjectPath, "-t:Install", $"-p:Configuration={_options.Configuration}", $"-p:AdbTarget=-s {_options.DeviceSerial}", "-nologo", "-v:m" })
            psi.ArgumentList.Add(a);
        _log($"deploy: dotnet {string.Join(' ', psi.ArgumentList)}");
        using var proc = Process.Start(psi) ?? throw new LaunchException("cannot start dotnet");
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        var output = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
        if (proc.ExitCode != 0)
            throw new LaunchException($"deploy failed (exit {proc.ExitCode}):{DeployHint(output)}\n{output}");
        _log("deploy: ok");
    }

    /// <summary>
    /// A one-line explanation for the install failures whose text points anywhere but at the cause.
    /// `INSTALL_FAILED_USER_RESTRICTED` is MIUI: either a confirmation dialog on the phone was
    /// not answered in time (it shows for a few seconds and then counts as refused), or the
    /// developer option "Install via USB" is off.
    /// </summary>
    internal static string DeployHint(string output)
    {
        if (output.Contains("INSTALL_FAILED_USER_RESTRICTED", StringComparison.Ordinal))
            return " the device refused the install (INSTALL_FAILED_USER_RESTRICTED). On Xiaomi/MIUI this is a confirmation " +
                   "dialog on the phone that nobody approved - watch the screen and retry - or Developer options > " +
                   "\"Install via USB\" being off; see README.md, \"What to enable on the device\".";
        if (output.Contains("INSTALL_FAILED_VERIFICATION_FAILURE", StringComparison.Ordinal))
            return " the device's app verifier blocked the install; disable \"Verify apps over USB\" in Developer options.";
        return "";
    }

    /// <summary>
    /// Starts the app with the agent enabled and returns when the main process' agent is
    /// listening (the property has already been rotated to the next port). Subsequent
    /// processes are reported through <see cref="AgentDetected"/>.
    /// </summary>
    public async Task<AgentReady> LaunchAsync(CancellationToken ct)
    {
        var serial = _options.DeviceSerial;
        var component = _app.ActivityName ?? await _adb.ResolveLauncherActivityAsync(serial, _app.PackageName, ct).ConfigureAwait(false);
        _log($"launch: {component} on {serial}");

        await _adb.ForceStopAsync(serial, _app.PackageName, ct).ConfigureAwait(false);
        var deviceNow = await _adb.GetDeviceEpochSecondsAsync(serial, ct).ConfigureAwait(false);
        await MeasureDeviceClockOffsetAsync(serial, ct).ConfigureAwait(false);
        _packageUid = await _adb.GetPackageUidAsync(serial, _app.PackageName, ct).ConfigureAwait(false);
        await WaitUntilPackageIsGoneAsync(serial, ct).ConfigureAwait(false);
        _deadline = deviceNow + (long)_options.EffectivePropertyLifetime.TotalSeconds;

        // Announce what is being taken over before taking it: see ForeignDebugPropertyWarning.
        if (ForeignDebugPropertyWarning(
                await _property.ReadAsync(ct).ConfigureAwait(false), deviceNow) is { } inUse)
            _log(inUse);

        await WritePropertyAsync(_nextPort, ct).ConfigureAwait(false);
        await ForwardAsync(_nextPort, ct).ConfigureAwait(false);

        await _adb.LogcatClearAsync(serial, ct).ConfigureAwait(false);
        // The clear alone is not trusted: on Android 11 images the buffer stays readable after it,
        // and the previous session's agent line would then be attached to as if it were this launch's.
        // The stream starts right after the newest line the buffer holds now; the process is only
        // started after this, so nothing of its own can be older than that.
        var logcatSince = AdbClient.LogcatSinceArgs(await _adb.ReadLogcatBoundaryAsync(serial, ct).ConfigureAwait(false));
        _logcatCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        var logcatCt = _logcatCts.Token;
        _logcatTask = Task.Run(() => _adb.StreamLogcatAsync(serial, line => OnLogcatLine(line, logcatCt), logcatCt, logcatSince), CancellationToken.None);

        await _adb.StartActivityAsync(serial, component, ct).ConfigureAwait(false);
        if (_options.KeepPropertyFresh)
        {
            _renewCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            var renewCt = _renewCts.Token;
            _renewTask = Task.Run(() => RenewPropertyLoopAsync(renewCt), CancellationToken.None);
            _log($"{DebugProperty} will be kept fresh for the whole session (other Mono apps starting meanwhile are affected)");
        }


        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.EffectiveConnectTimeout);
        try
        {
            return await _firstAgent.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LaunchException($"no debugger agent from {_app.PackageName} within {_options.EffectiveConnectTimeout.TotalSeconds:F0}s (is the app a Debug build with the Mono agent enabled?)");
        }
    }

    /// <summary>Clears the property, removes the forwards, stops the package and the logcat reader.</summary>
    public async Task ShutdownAsync(CancellationToken ct)
    {
        var serial = _options.DeviceSerial;
        // Order matters. The logcat reader rotates the property whenever a process of ours starts,
        // so it has to be stopped *before* the property is cleared - otherwise a process Android is
        // still restarting makes the reader publish a fresh port after the clear, and the device is
        // left carrying a stale `debug.mono.extra`. The flag closes the same window for a line that
        // is already being handled.
        _shuttingDown = true;
        await StopRenewalAsync().ConfigureAwait(false);
        await StopLogcatAsync().ConfigureAwait(false);

        // Cleared, not restored: a value left on the device would make every Mono app started afterwards wait
        // for a debugger on our port, and what was there before this launch is never worth keeping.
        try { await _property.ClearAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { _log($"shutdown: clearing {DebugProperty} failed: {ex.Message}"); }

        try { await _adb.ForceStopAsync(serial, _app.PackageName, ct).ConfigureAwait(false); }
        catch (Exception ex) { _log($"shutdown: force-stop failed: {ex.Message}"); }

        int[] ports;
        lock (_gate) { ports = _forwardedPorts.ToArray(); _forwardedPorts.Clear(); }
        foreach (var p in ports)
        {
            try { await _adb.RemoveForwardAsync(serial, p, ct).ConfigureAwait(false); }
            catch (Exception ex) { _log($"shutdown: remove forward {p} failed: {ex.Message}"); }
        }
    }


    private async Task StopRenewalAsync()
    {
        var cts = _renewCts;
        var task = _renewTask;
        _renewCts = null;
        _renewTask = null;
        if (cts is null) return;
        try
        {
            await cts.CancelAsync().ConfigureAwait(false);
            if (task is not null) await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log($"stopping the property refresh: {ex.Message}"); }
        finally { cts.Dispose(); }
    }
    public async ValueTask DisposeAsync()
    {
        await StopRenewalAsync().ConfigureAwait(false);
        await StopLogcatAsync().ConfigureAwait(false);
    }

    private async Task StopLogcatAsync()
    {
        var cts = _logcatCts;
        var task = _logcatTask;
        _logcatCts = null;
        _logcatTask = null;
        if (cts is null) return;
        cts.Cancel();
        if (task is not null)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception ex) { _log($"logcat reader stop: {ex.Message}"); }
        }
        cts.Dispose();
    }


    /// <summary>
    /// What a <c>debug.mono.extra</c> already on the device means for this launch, or null when
    /// there is nothing worth saying: no value, or one whose deadline has passed and which
    /// therefore no longer diverts anything.
    /// <para>
    /// The property is device-global, so two debuggers on one device overwrite each other in
    /// silence and the loser's app hangs for the agent timeout on a port nobody listens on. That
    /// symptom is far from its cause, which is why taking the property over is announced.
    /// </para>
    /// </summary>
    /// <param name="value">The property as read from the device.</param>
    /// <param name="deviceEpochSeconds">The device clock, in the unit the property's own deadline uses.</param>
    public static string? ForeignDebugPropertyWarning(string? value, long deviceEpochSeconds)
        => DevicePropertyOverride.ForeignValueWarning(DebugProperty, value, deviceEpochSeconds);
    private async Task WritePropertyAsync(int port, CancellationToken ct)
    {
        // Rotation (logcat thread), launch and the renewal loop all write this property; keep the
        // deadline and the value they publish consistent.
        await _propertyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var value = $"debug=127.0.0.1:{port},timeout={_deadline},loglevel={_options.AgentLogLevel},server=y";
            await _property.ApplyAsync(value, ct).ConfigureAwait(false);
            _log($"{DebugProperty} = {value}");
        }
        finally { _propertyGate.Release(); }
    }

    /// <summary>
    /// Keeps the property's deadline in the future for as long as the session lives, so a process
    /// the app starts much later still finds a debugger waiting. Off by default: the same freshness
    /// is what lets an unrelated Mono app pick up our port (the launcher refuses it and rotates,
    /// but that app has still lost 30 seconds waiting for a debugger that never comes).
    /// </summary>
    private async Task RenewPropertyLoopAsync(CancellationToken ct)
    {
        var lifetime = _options.EffectivePropertyLifetime;
        var period = TimeSpan.FromSeconds(Math.Max(20, lifetime.TotalSeconds / 3));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(period, ct).ConfigureAwait(false);
                var deviceNow = await _adb.GetDeviceEpochSecondsAsync(_options.DeviceSerial, ct).ConfigureAwait(false);
                _deadline = deviceNow + (long)lifetime.TotalSeconds;
                await WritePropertyAsync(_nextPort, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // The device may be busy or gone; the next iteration retries.
                _log($"refreshing {DebugProperty} failed: {ex.Message}");
            }
        }
    }
    private async Task ForwardAsync(int port, CancellationToken ct)
    {
        await _adb.ForwardAsync(_options.DeviceSerial, port, port, ct).ConfigureAwait(false);
        lock (_gate) _forwardedPorts.Add(port);
    }

    private void OnLogcatLine(string line, CancellationToken ct)
    {
        var m = LogcatLine.Match(line);
        if (!m.Success) return;
        var pid = int.Parse(m.Groups["pid"].Value);
        var tag = m.Groups["tag"].Value;
        var msg = m.Groups["msg"].Value;

        if (tag == "ActivityManager")
        {
            var sp = StartProc.Match(msg);
            if (sp.Success)
            {
                var name = sp.Groups["name"].Value;
                // ActivityManager prints the uid right after the process name, which is the only
                // thing that identifies a process declared with a global `android:process`.
                if (BelongsToPackage(name) || ParseAmUid(sp.Groups["uid"].Value) == _packageUid)
                {
                    var npid = int.Parse(sp.Groups["pid"].Value);
                    lock (_gate) { _processNames[npid] = name; _appPids.Add(npid); }
                    // Rotate as soon as a process of ours starts, not only when its agent
                    // announces itself: the property is device-global, and everything between
                    // those two moments is a window in which the next process reads the same
                    // port and loses its agent. ActivityManager logs this line at fork, long
                    // before the runtime reads the property. Rotating early cannot steal the
                    // port from this process - it reads whatever is current, and its agent tells
                    // us which port it actually took.
                    RotateOnly(_nextPort, ct);
                }
                return;
            }
            if (msg.Contains(" has died", StringComparison.Ordinal) && msg.Contains(_app.PackageName, StringComparison.Ordinal))
            {
                var dm = Regex.Match(msg, @"\(pid (?<pid>\d+)\)");
                if (dm.Success)
                {
                    var dpid = int.Parse(dm.Groups["pid"].Value);
                    _log($"process {dpid} died: {msg}");
                    ProcessDied?.Invoke(dpid);
                }
                return;
            }
        }

        bool known;
        lock (_gate) known = _appPids.Contains(pid);

        if (tag == "monodroid-debug")
        {
            var am = AgentInit.Match(msg);
            if (am.Success)
            {
                var port = int.Parse(am.Groups["port"].Value);
                string? name;
                lock (_gate) _processNames.TryGetValue(pid, out name);
                int? uid = null;
                if (name is null)
                {
                    // ActivityManager did not announce this pid (its line can be missed when the
                    // logcat reader starts late). `ps` is the fallback. Keep this path short: the
                    // property is rotated only after this decision, and every millisecond spent
                    // here is a millisecond in which another process can read the same port.
                    (name, uid) = LookupProcess(pid, ct);
                }
                var ours = IsOurs(name, uid, pid, ct);
                if (!ours)
                {
                    // debug.mono.extra is device-global: any Mono app process that starts while our
                    // property is fresh reads it and waits for a debugger on our port. It is not our
                    // debuggee; do not attach it (it exits by itself after the agent's 30 s timeout).
                    // The port it took is burnt, so rotate anyway.
                    _log($"WARNING: foreign Mono process pid {pid} ({name ?? "unknown"}) picked up the debug property on port {port}; not attaching. Keep PropertyLifetime short to narrow this window.");
                    RotateOnly(port, ct);
                    return;
                }
                lock (_gate) { _appPids.Add(pid); _processNames[pid] = name!; }
                _log($"agent listening: pid {pid} ({name}) port {port}");
                // Rotate before anyone connects: the next process of the package must see a free port.
                RotateAndAnnounce(new AgentReady(pid, port, name), ct);
                return;
            }
            if (msg.Contains("Not starting the debugger", StringComparison.Ordinal))
                _log($"pid {pid}: {msg}");
        }
        else if (tag == "mono" && msg.Contains("debugger-agent", StringComparison.Ordinal))
        {
            _log($"pid {pid}: {msg}");
        }

        if (known)
        {
            var handler = AppOutput;
            if (handler is not null)
            {
                var tid = int.TryParse(m.Groups["tid"].Value, out var t) ? t : 0;
                var level = m.Groups["lvl"].Value is { Length: > 0 } l ? l[0] : 'I';
                handler(new AppLogLine(ParseLogcatTimestamp(m.Groups["stamp"].Value), pid, tid, level, tag, msg));
            }
        }
    }

    /// <summary>
    /// `am force-stop` returns before the processes are actually gone, and a sticky service Android
    /// is restarting can still be on its way up. Any of them still alive when we publish the port
    /// reads the property and takes it, and the process we do want loses its agent. Waits a few
    /// seconds; if something refuses to die we go ahead anyway and say so.
    /// </summary>
    private async Task WaitUntilPackageIsGoneAsync(string serial, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            IReadOnlyList<(int Pid, string Name)> alive;
            try { alive = await _adb.ListPackageProcessesAsync(serial, _app.PackageName, _packageUid, ct).ConfigureAwait(false); }
            catch (Exception ex) { _log($"could not check for leftover processes: {ex.Message}"); return; }
            if (alive.Count == 0) return;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        _log($"WARNING: processes of {_app.PackageName} are still alive after force-stop; they may take the debug port");
    }

    /// <summary>
    /// Measures <see cref="DeviceClockOffset"/>. `date` has one-second resolution, so the reading
    /// is taken in the middle of the round trip and the result is rounded to the nearest second:
    /// what matters is the timezone difference, not sub-second precision.
    /// </summary>
    private async Task MeasureDeviceClockOffsetAsync(string serial, CancellationToken ct)
    {
        try
        {
            var before = DateTime.Now;
            var deviceTime = await _adb.GetDeviceLocalTimeAsync(serial, ct).ConfigureAwait(false);
            var hostTime = before + (DateTime.Now - before) / 2;
            var offset = deviceTime - hostTime;
            DeviceClockOffset = TimeSpan.FromSeconds(Math.Round(offset.TotalSeconds));
            if (DeviceClockOffset != TimeSpan.Zero)
                _log($"device clock differs from the host clock by {DeviceClockOffset}; app output is reported on the device clock");
        }
        catch (Exception ex)
        {
            // Not worth failing a launch for: timestamps stay on the host clock.
            _log($"could not measure the device clock offset: {ex.Message}");
            DeviceClockOffset = TimeSpan.Zero;
        }
    }

    /// <summary>
    /// logcat's threadtime stamps carry no year (`08-20 09:10:39.891`) and are in device local
    /// time; assume the device's current year and fall back to the device clock when it does not
    /// parse.
    /// </summary>
    private DateTime ParseLogcatTimestamp(string stamp)
    {
        var deviceNow = DeviceNow;
        return DateTime.TryParseExact($"{deviceNow.Year}-{stamp}", "yyyy-MM-dd HH:mm:ss.fff",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : deviceNow;
    }

    /// <summary>
    /// Turns the uid ActivityManager prints after a process name into a Linux uid: <c>u0a174</c>
    /// is app 174 of user 0, i.e. 10174; a plain number (system processes) is the uid itself.
    /// Returns null for anything else.
    /// </summary>
    private static int? ParseAmUid(string token)
    {
        if (int.TryParse(token, out var plain)) return plain;
        var m = AmUid.Match(token);
        if (!m.Success) return null;
        var user = int.Parse(m.Groups["user"].Value);
        var app = int.Parse(m.Groups["app"].Value);
        return user * 100000 + 10000 + app;
    }

    /// <summary>
    /// True when the process belongs to the app under debug. The name settles it for the main
    /// process and for `package:suffix` helpers; a component declared with a global
    /// <c>android:process</c> name (the reference application's `the app's own android:process`, for instance) carries a name
    /// of its own, and only the uid identifies it. The uid is looked up on demand, so the common
    /// case costs nothing.
    /// </summary>
    private bool IsOurs(string? processName, int? uid, int pid, CancellationToken ct)
    {
        if (processName is not null && BelongsToPackage(processName)) return true;
        if (_packageUid is null) return false;
        uid ??= LookupProcess(pid, ct).Uid;
        return uid == _packageUid;
    }

    private bool BelongsToPackage(string processName)
        => processName == _app.PackageName || processName.StartsWith(_app.PackageName + ":", StringComparison.Ordinal);

    /// <summary>
    /// Resolves a process through `ps` (synchronous; called on the logcat thread only when
    /// ActivityManager did not announce the pid). Returns its name and its uid.
    /// </summary>
    private (string? Name, int? Uid) LookupProcess(int pid, CancellationToken ct)
    {
        try
        {
            var outp = _adb.ShellAsync(_options.DeviceSerial, "ps -A -o PID,UID,NAME", ct, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            foreach (var raw in outp.Split('\n'))
            {
                var parts = raw.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length != 3 || !int.TryParse(parts[0], out var p) || p != pid) continue;
                return (parts[2], int.TryParse(parts[1], out var uid) ? uid : null);
            }
        }
        catch (Exception ex) { _log($"ps lookup for pid {pid} failed: {ex.Message}"); }
        return (null, null);
    }

    /// <summary>
    /// Publishes the next port. <paramref name="takenPort"/> is the one that must not be handed
    /// out again; when it is already behind us the property is left alone, so the common case of
    /// being called twice for the same process costs nothing.
    /// This runs on the logcat thread, ahead of the rotation window, so it does exactly one adb
    /// call: the forward is created later, for the port a process actually took.
    /// </summary>
    private void RotateOnly(int takenPort, CancellationToken ct)
    {
        try
        {
            if (_shuttingDown || takenPort < _nextPort) return;
            _nextPort = takenPort + 1;
            WritePropertyAsync(_nextPort, ct).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log($"port rotation to {_nextPort} failed: {ex.Message}");
        }
    }

    private void RotateAndAnnounce(AgentReady ready, CancellationToken ct)
    {
        // Runs on the logcat reader thread; keep it synchronous so the rotation is done
        // before the event (and therefore before any connect) happens.
        RotateOnly(ready.Port, ct);
        // The forward is for the port this process actually took, created now rather than for
        // every port the rotation ever published: one per attached process, not one per fork.
        try { ForwardAsync(ready.Port, ct).GetAwaiter().GetResult(); }
        catch (Exception ex) { _log($"forwarding port {ready.Port} for pid {ready.Pid} failed: {ex.Message}"); }
        _firstAgent.TrySetResult(ready);
        try { AgentDetected?.Invoke(ready); }
        catch (Exception ex) { _log($"AgentDetected handler failed: {ex}"); }
    }
}
