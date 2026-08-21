using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using NetAndroidDebugger.Core.Adb;

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
    private const string DebugProperty = "debug.mono.extra";

    // threadtime: "08-20 09:10:39.891  6954  6954 W monodroid-debug: Trying to initialize ..."
    private static readonly Regex LogcatLine = new(
        @"^(?<stamp>\d\d-\d\d\s+\d\d:\d\d:\d\d\.\d+)\s+(?<pid>\d+)\s+(?<tid>\d+)\s+(?<lvl>[VDIWEFS])\s+(?<tag>.*?)\s*:\s(?<msg>.*)$",
        RegexOptions.Compiled);
    private static readonly Regex AgentInit = new(@"Trying to initialize the debugger with options:.*address=127\.0\.0\.1:(?<port>\d+)", RegexOptions.Compiled);
    private static readonly Regex StartProc = new(@"Start proc (?<pid>\d+):(?<name>\S+?)/", RegexOptions.Compiled);

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
    private bool _propertyOwned;
    private int? _packageUid;

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
            throw new LaunchException($"deploy failed (exit {proc.ExitCode}):\n{output}");
        _log("deploy: ok");
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
        _deadline = deviceNow + (long)_options.EffectivePropertyLifetime.TotalSeconds;

        await WritePropertyAsync(_nextPort, ct).ConfigureAwait(false);
        await ForwardAsync(_nextPort, ct).ConfigureAwait(false);

        await _adb.LogcatClearAsync(serial, ct).ConfigureAwait(false);
        _logcatCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        var logcatCt = _logcatCts.Token;
        _logcatTask = Task.Run(() => _adb.StreamLogcatAsync(serial, line => OnLogcatLine(line, logcatCt), logcatCt), CancellationToken.None);

        await _adb.StartActivityAsync(serial, component, ct).ConfigureAwait(false);

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
        try
        {
            if (_propertyOwned)
            {
                await _adb.SetPropAsync(serial, DebugProperty, "", ct).ConfigureAwait(false);
                _propertyOwned = false;
            }
        }
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

        await StopLogcatAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
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

    private async Task WritePropertyAsync(int port, CancellationToken ct)
    {
        var value = $"debug=127.0.0.1:{port},timeout={_deadline},loglevel={_options.AgentLogLevel},server=y";
        await _adb.SetPropAsync(_options.DeviceSerial, DebugProperty, value, ct).ConfigureAwait(false);
        _propertyOwned = true;
        _log($"{DebugProperty} = {value}");
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
                if (name == _app.PackageName || name.StartsWith(_app.PackageName + ":", StringComparison.Ordinal))
                {
                    var npid = int.Parse(sp.Groups["pid"].Value);
                    lock (_gate) { _processNames[npid] = name; _appPids.Add(npid); }
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
                    (name, uid) = LookupProcess(pid, ct);
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
    private void RotateOnly(int takenPort, CancellationToken ct)
    {
        try
        {
            if (takenPort >= _nextPort)
                _nextPort = takenPort + 1;
            WritePropertyAsync(_nextPort, ct).GetAwaiter().GetResult();
            ForwardAsync(_nextPort, ct).GetAwaiter().GetResult();
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
        _firstAgent.TrySetResult(ready);
        try { AgentDetected?.Invoke(ready); }
        catch (Exception ex) { _log($"AgentDetected handler failed: {ex}"); }
    }
}
