using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace NetAndroid.Device;

/// <summary>Result of one adb invocation.</summary>
public sealed record AdbResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>The same as <see cref="Succeeded"/>, under the name the profiler's call sites use.</summary>
    public bool Success => Succeeded;
}

/// <summary>
/// Thin wrapper over the adb executable, shared by the debugger and the profiler: the union of what
/// the two products' clients offered, on top of <see cref="ProcessRunner"/>. Every device-bound call
/// takes an explicit serial: nothing here relies on the adb default device, because other emulators
/// or devices may be attached. A command that fails throws <see cref="AdbException"/>, which is also
/// a <see cref="ToolException"/>.
/// </summary>
public sealed class AdbClient
{
    private static readonly Regex DeviceLine = new(@"^(?<serial>\S+)\s+(?<state>\S+)(?<rest>.*)$", RegexOptions.Compiled);
    private static readonly Regex LogcatStamp = new(@"^(?<md>\d\d-\d\d) (?<time>\d\d:\d\d:\d\d\.\d\d\d)", RegexOptions.Compiled);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Wraps the adb at <paramref name="adbPath"/>, or the one <see cref="AdbLocator"/> finds when
    /// none is given. Nothing here assumes adb is on PATH.
    /// </summary>
    /// <exception cref="AdbNotFoundException">adb cannot be found, or the given path has nothing at it.</exception>
    public AdbClient(string? adbPath = null, string adbPathSource = "the call")
    {
        Location = AdbLocator.Locate(adbPath, adbPathSource);
    }

    /// <summary>Where this client's adb is and which source named it.</summary>
    public AdbLocation Location { get; }

    public string AdbPath => Location.Path;

    // ------------------------------------------------------------------ running adb

    /// <summary>Runs <c>adb [args]</c> (no serial) and returns the result without throwing on non-zero exit.</summary>
    public Task<AdbResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
        => RunCoreAsync(args, ct, timeout ?? DefaultTimeout);

    /// <summary>Runs <c>adb -s serial [args]</c> and returns the result without throwing on non-zero exit.</summary>
    public Task<AdbResult> RunAsync(string serial, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
        => RunCoreAsync(["-s", serial, .. args], ct, timeout ?? DefaultTimeout);

    /// <summary>Runs <c>adb -s serial [args]</c>; throws <see cref="AdbException"/> on non-zero exit.</summary>
    public async Task<AdbResult> RunDeviceAsync(string serial, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
    {
        var r = await RunAsync(serial, args, ct, timeout).ConfigureAwait(false);
        if (!r.Succeeded)
            throw new AdbException($"adb -s {serial} {string.Join(' ', args)} failed ({r.ExitCode}): {r.StdErr.Trim()} {r.StdOut.Trim()}".Trim());
        return r;
    }

    /// <summary>The same as <see cref="RunDeviceAsync"/>, under the name the profiler's call sites use.</summary>
    public Task<AdbResult> RunCheckedAsync(string serial, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
        => RunDeviceAsync(serial, args, ct, timeout);

    /// <summary>
    /// Runs <c>adb -s serial [args]</c> and returns raw stdout bytes (for <c>exec-out</c>, whose
    /// output is binary and must not go through text decoding). Throws on non-zero exit.
    /// </summary>
    public async Task<byte[]> RunDeviceBytesAsync(string serial, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
    {
        var effective = timeout ?? DefaultTimeout;
        ProcessBytesResult r;
        try
        {
            r = await ProcessRunner.RunBytesAsync(AdbPath, ["-s", serial, .. args], ct, effective).ConfigureAwait(false);
        }
        catch (ToolException ex) when (ex is not AdbException)
        {
            throw new AdbException($"adb -s {serial} {string.Join(' ', args)} {Describe(ex, effective)}", ex);
        }
        if (r.ExitCode != 0)
            throw new AdbException($"adb -s {serial} {string.Join(' ', args)} failed ({r.ExitCode}): {r.StdErr.Trim()}");
        return r.StdOut;
    }

    /// <summary>adb exec-out: binary-safe stdout of a device command. Throws on non-zero exit.</summary>
    public Task<byte[]> ExecOutAsync(string serial, string command, CancellationToken ct)
        => RunDeviceBytesAsync(serial, ["exec-out", command], ct);

    /// <summary>Runs <c>adb -s serial shell command</c> and returns stdout, trimmed. The command string reaches the device shell as-is.</summary>
    public async Task<string> ShellAsync(string serial, string command, CancellationToken ct, TimeSpan? timeout = null)
    {
        var r = await RunDeviceAsync(serial, ["shell", command], ct, timeout).ConfigureAwait(false);
        return r.StdOut.Trim();
    }

    // ------------------------------------------------------------------ devices

    /// <summary>
    /// The devices adb sees. An online device is described further (model, API level, ABI, AVD
    /// name) with a few property reads; one sick device must not hide the others, so a device that
    /// does not answer them is listed with what <c>adb devices -l</c> said about it.
    /// </summary>
    public async Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken ct)
    {
        var r = await RunAsync(["devices", "-l"], ct).ConfigureAwait(false);
        if (!r.Succeeded)
            throw new AdbException($"adb devices failed ({r.ExitCode}): {r.StdErr.Trim()}");
        var list = new List<DeviceInfo>();
        foreach (var raw in r.StdOut.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.Ordinal) || line.StartsWith('*'))
                continue;
            var m = DeviceLine.Match(line);
            if (!m.Success) continue;
            var serial = m.Groups["serial"].Value;
            var state = m.Groups["state"].Value;
            var isEmulator = serial.StartsWith("emulator-", StringComparison.Ordinal);
            var listedModel = Regex.Match(m.Groups["rest"].Value, @"model:(\S+)") is { Success: true } mm ? mm.Groups[1].Value : null;
            if (state != "device")
            {
                list.Add(new DeviceInfo(serial, state, listedModel, isEmulator));
                continue;
            }
            var model = await TryGetPropAsync(serial, "ro.product.model", ct).ConfigureAwait(false);
            var sdk = await TryGetPropAsync(serial, "ro.build.version.sdk", ct).ConfigureAwait(false);
            var abi = await TryGetPropAsync(serial, "ro.product.cpu.abi", ct).ConfigureAwait(false);
            string? avd = null;
            if (isEmulator)
            {
                var a = await RunAsync(serial, ["emu", "avd", "name"], ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                avd = a.StdOut.Split('\n').FirstOrDefault()?.Trim();
                if (string.IsNullOrEmpty(avd) || avd.StartsWith("OK", StringComparison.Ordinal)) avd = null;
            }
            list.Add(new DeviceInfo(serial, state, model.Length > 0 ? model : listedModel, isEmulator, avd,
                int.TryParse(sdk, out var api) ? api : 0, abi));
        }
        return list;
    }

    /// <summary>getprop that answers an empty string instead of throwing when the device is unusable.</summary>
    private async Task<string> TryGetPropAsync(string serial, string name, CancellationToken ct)
    {
        try
        {
            var r = await RunAsync(serial, ["shell", $"getprop {name}"], ct, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            return r.Succeeded ? r.StdOut.Trim() : "";
        }
        catch (Exception e) when (e is ToolException or TimeoutException)
        {
            return "";
        }
    }

    // ------------------------------------------------------------------ ports

    public Task ForwardAsync(string serial, int hostPort, int devicePort, CancellationToken ct)
        => RunDeviceAsync(serial, ["forward", $"tcp:{hostPort}", $"tcp:{devicePort}"], ct);

    public async Task RemoveForwardAsync(string serial, int hostPort, CancellationToken ct)
    {
        // Not fatal when the forward no longer exists.
        await RunAsync(serial, ["forward", "--remove", $"tcp:{hostPort}"], ct, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
    }

    public Task ReverseAsync(string serial, int devicePort, int hostPort, CancellationToken ct)
        => RunDeviceAsync(serial, ["reverse", $"tcp:{devicePort}", $"tcp:{hostPort}"], ct);

    public Task ReverseRemoveAsync(string serial, int devicePort, CancellationToken ct)
        => RunAsync(serial, ["reverse", "--remove", $"tcp:{devicePort}"], ct);

    // ------------------------------------------------------------------ properties and clock

    /// <summary>setprop; an empty value clears the property.</summary>
    public Task SetPropAsync(string serial, string name, string value, CancellationToken ct)
        // An empty value needs the quotes to survive `adb shell` argument joining.
        => ShellAsync(serial, value.Length == 0 ? $"setprop {name} ''" : $"setprop {name} '{value}'", ct);

    public async Task<string> GetPropAsync(string serial, string name, CancellationToken ct)
        => (await ShellAsync(serial, $"getprop {name}", ct).ConfigureAwait(false)).Trim();

    /// <summary>Unix epoch seconds according to the device clock (a freshness deadline must use it, not the host clock).</summary>
    public async Task<long> GetDeviceEpochSecondsAsync(string serial, CancellationToken ct)
    {
        var s = (await ShellAsync(serial, "date +%s", ct).ConfigureAwait(false)).Trim();
        if (!long.TryParse(s, out var v))
            throw new AdbException($"unexpected `date +%s` output on {serial}: '{s}'");
        return v;
    }

    /// <summary>
    /// Reads the device's wall clock (local time, the same clock logcat stamps its lines with)
    /// and returns it as a naive <see cref="DateTime"/>.
    /// </summary>
    public async Task<DateTime> GetDeviceLocalTimeAsync(string serial, CancellationToken ct)
    {
        var s = (await ShellAsync(serial, "date \"+%Y-%m-%d %H:%M:%S\"", ct).ConfigureAwait(false)).Trim();
        if (!DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
            throw new AdbException($"unexpected `date` output on {serial}: '{s}'");
        return parsed;
    }

    // ------------------------------------------------------------------ packages and processes

    /// <summary>Resolves the launcher activity of a package as <c>pkg/fully.qualified.Name</c>.</summary>
    public async Task<string> ResolveLauncherActivityAsync(string serial, string package, CancellationToken ct)
    {
        var outp = await ShellAsync(serial, $"cmd package resolve-activity --brief {package}", ct).ConfigureAwait(false);
        var lines = outp.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var last = lines.LastOrDefault();
        if (last is null || !last.Contains('/'))
            throw new AdbException($"cannot resolve launcher activity of {package} on {serial}: '{outp.Trim()}'");
        return last;
    }

    public async Task StartActivityAsync(string serial, string component, CancellationToken ct)
    {
        var outp = await ShellAsync(serial, $"am start -n {component}", ct).ConfigureAwait(false);
        if (outp.Contains("Error", StringComparison.OrdinalIgnoreCase))
            throw new AdbException($"am start -n {component} failed on {serial}: {outp.Trim()}");
    }

    public Task ForceStopAsync(string serial, string package, CancellationToken ct)
        => ShellAsync(serial, $"am force-stop {package}", ct);

    /// <summary>
    /// Launch the app and wait until its process actually exists.
    ///
    /// Neither launch method is reliable on its own. A force-stop leaves the package in
    /// `stopped=true`, and a plain `am start -n` is then accepted without ever forking the
    /// process - nothing is logged, the caller just waits. A launcher-style monkey intent
    /// clears that state, but has been observed doing the same: the START intent appears in
    /// logcat and no process follows. Both were mistaken for app-level failures in the past
    /// (an app that "will not start"), so the launch is verified here instead: try one way,
    /// wait for a pid, try the other, and only then give up with what was attempted.
    /// </summary>
    public async Task LaunchAsync(string serial, string package, CancellationToken ct)
    {
        var attempts = new List<string>();
        for (int round = 0; round < 2; round++)
        {
            if (await TryMonkeyAsync(serial, package, attempts, ct).ConfigureAwait(false)) return;
            if (await TryExplicitStartAsync(serial, package, attempts, ct).ConfigureAwait(false)) return;
        }
        throw new AdbException($"{package} did not start on {serial}: " + string.Join("; ", attempts));
    }

    private async Task<bool> TryMonkeyAsync(string serial, string package, List<string> attempts, CancellationToken ct)
    {
        var r = await RunAsync(serial, ["shell", $"monkey -p {package} -c android.intent.category.LAUNCHER 1"], ct).ConfigureAwait(false);
        if (r.StdOut.Contains("No activities found", StringComparison.OrdinalIgnoreCase))
        {
            attempts.Add("monkey: no launcher activity");
            return false;
        }
        if (!r.Succeeded || !r.StdOut.Contains("Events injected", StringComparison.OrdinalIgnoreCase))
        {
            attempts.Add("monkey: intent not injected");
            return false;
        }
        if (await WaitForProcessAsync(serial, package, ct).ConfigureAwait(false)) return true;
        attempts.Add("monkey: intent injected but no process appeared");
        return false;
    }

    private async Task<bool> TryExplicitStartAsync(string serial, string package, List<string> attempts, CancellationToken ct)
    {
        var resolve = await RunAsync(serial, ["shell", $"cmd package resolve-activity --brief -c android.intent.category.LAUNCHER {package}"], ct).ConfigureAwait(false);
        string? component = resolve.StdOut.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(package + "/", StringComparison.Ordinal));
        if (component is null)
        {
            attempts.Add("am start: no launcher activity (is the package installed?)");
            return false;
        }
        // No -W: waiting for the activity to go idle can take minutes on an instrumented app.
        var start = await RunAsync(serial, ["shell", $"am start -n {component}"], ct, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        if (!start.Succeeded || start.StdOut.Contains("Error", StringComparison.OrdinalIgnoreCase))
        {
            attempts.Add($"am start {component}: {start.StdOut.Trim()}{start.StdErr.Trim()}");
            return false;
        }
        if (await WaitForProcessAsync(serial, package, ct).ConfigureAwait(false)) return true;
        attempts.Add($"am start {component}: accepted but no process appeared");
        return false;
    }

    /// <summary>Poll for the app's process; a cold start on a loaded emulator takes seconds.</summary>
    private async Task<bool> WaitForProcessAsync(string serial, string package, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (await PidOfAsync(serial, package, ct).ConfigureAwait(false) is not null) return true;
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        }
        return false;
    }

    public async Task<int?> PidOfAsync(string serial, string package, CancellationToken ct)
    {
        var r = await RunAsync(serial, ["shell", $"pidof {package}"], ct).ConfigureAwait(false);
        var first = r.StdOut.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return int.TryParse(first, out int pid) ? pid : null;
    }

    /// <summary>
    /// Linux uid the package's processes run under, or null when the package is not installed.
    /// A process of the app can carry a name unrelated to the package (a component declared with a
    /// global <c>android:process</c>), and its uid is what still identifies it as ours.
    /// </summary>
    public async Task<int?> GetPackageUidAsync(string serial, string package, CancellationToken ct)
    {
        var outp = await ShellAsync(serial, $"pm list packages -U {package}", ct).ConfigureAwait(false);
        foreach (var raw in outp.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("package:", StringComparison.Ordinal)) continue;
            var rest = line["package:".Length..];
            var sep = rest.IndexOf(" uid:", StringComparison.Ordinal);
            // `pm list packages` matches by substring, so only the exact package counts.
            if (sep < 0 || !string.Equals(rest[..sep], package, StringComparison.Ordinal)) continue;
            if (int.TryParse(rest[(sep + " uid:".Length)..].Trim(), out var uid)) return uid;
        }
        return null;
    }

    /// <summary>
    /// Processes of the app: those named <paramref name="package"/> or <c>package:suffix</c>, plus
    /// any other process running under the package's uid (components declared with a global
    /// <c>android:process</c> name). Apps sharing a uid would also match, which is intended: they
    /// share the sandbox a debugger drives.
    /// </summary>
    public Task<IReadOnlyList<(int Pid, string Name)>> ListPackageProcessesAsync(string serial, string package, CancellationToken ct)
        => ListPackageProcessesAsync(serial, package, null, ct);

    /// <summary>
    /// Same, with the package uid already known (saves a `pm list packages` round trip when this is
    /// called in a loop). Pass null to have it resolved.
    /// </summary>
    public async Task<IReadOnlyList<(int Pid, string Name)>> ListPackageProcessesAsync(string serial, string package, int? uid, CancellationToken ct)
    {
        uid ??= await GetPackageUidAsync(serial, package, ct).ConfigureAwait(false);
        var outp = await ShellAsync(serial, "ps -A -o PID,UID,NAME", ct).ConfigureAwait(false);
        var result = new List<(int, string)>();
        foreach (var raw in outp.Split('\n'))
        {
            var parts = raw.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 3 || !int.TryParse(parts[0], out var pid)) continue;
            var name = parts[2];
            var matchesName = name == package || name.StartsWith(package + ":", StringComparison.Ordinal);
            var matchesUid = uid is not null && int.TryParse(parts[1], out var puid) && puid == uid;
            if (matchesName || matchesUid)
                result.Add((pid, name));
        }
        return result;
    }

    /// <summary>Paths of the installed APK(s) of a package (base + splits), empty when not installed.</summary>
    public async Task<IReadOnlyList<string>> PackagePathsAsync(string serial, string package, CancellationToken ct)
    {
        var r = await RunAsync(serial, ["shell", $"pm path {package}"], ct).ConfigureAwait(false);
        return r.StdOut.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("package:", StringComparison.Ordinal)).Select(l => l["package:".Length..]).ToList();
    }

    /// <summary>True when <c>run-as</c> works for the package, i.e. the app is debuggable.</summary>
    public async Task<bool> IsDebuggableAsync(string serial, string package, CancellationToken ct)
    {
        var r = await RunAsync(serial, ["shell", $"run-as {package} id"], ct).ConfigureAwait(false);
        return r.Succeeded && r.StdOut.Contains("uid=", StringComparison.Ordinal);
    }

    /// <summary>Run a command inside the app sandbox (debuggable apps only). Returns stdout; throws on failure.</summary>
    public async Task<string> RunAsAsync(string serial, string package, string command, CancellationToken ct)
    {
        var r = await RunAsync(serial, ["shell", $"run-as {package} sh -c '{command.Replace("'", "'\\''")}'"], ct).ConfigureAwait(false);
        if (!r.Succeeded) throw new AdbException($"run-as {package} '{command}' failed: {r.StdErr.Trim()} {r.StdOut.Trim()}".Trim());
        return r.StdOut.Trim();
    }

    // ------------------------------------------------------------------ files

    public Task PushAsync(string serial, string localPath, string remotePath, CancellationToken ct)
        => RunDeviceAsync(serial, ["push", localPath, remotePath], ct, TimeSpan.FromMinutes(5));

    public Task PullAsync(string serial, string remotePath, string localPath, CancellationToken ct)
        => RunDeviceAsync(serial, ["pull", remotePath, localPath], ct, TimeSpan.FromMinutes(10));

    // ------------------------------------------------------------------ logcat

    /// <summary>Clears the logcat buffer. Not fatal when the device refuses: <see cref="ReadLogcatBoundaryAsync"/> is what a reader relies on.</summary>
    public Task LogcatClearAsync(string serial, CancellationToken ct)
        => RunAsync(serial, ["logcat", "-c"], ct);

    /// <summary>
    /// The instant after which a logcat line is new: one millisecond past the newest line the buffer
    /// holds right now, in logcat's own <c>MM-DD hh:mm:ss.mmm</c> stamp. Null when the buffer is empty.
    /// <para>
    /// <c>logcat -c</c> is not trusted to clear: on Android 11 images the buffer stays readable
    /// after it (measured 2026-09-05), and a launch then read the previous process' agent line as
    /// if it were its own. The device clock is no boundary either - it is read at one-second
    /// resolution, and two launches in a row fit in one second. The buffer's own stamps are exact.
    /// </para>
    /// </summary>
    public async Task<string?> ReadLogcatBoundaryAsync(string serial, CancellationToken ct)
    {
        var r = await RunDeviceAsync(serial, ["logcat", "-v", "threadtime", "-d", "-t", "1"], ct, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        foreach (var raw in r.StdOut.Split('\n'))
        {
            var m = LogcatStamp.Match(raw.Trim());
            if (!m.Success) continue;
            var stamp = DateTime.ParseExact(m.Groups["md"].Value + " " + m.Groups["time"].Value, "MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            return stamp.AddMilliseconds(1).ToString("MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }
        return null;
    }

    /// <summary>Arguments that make a logcat stream start at a boundary from <see cref="ReadLogcatBoundaryAsync"/>; none when there is no boundary.</summary>
    public static IReadOnlyList<string>? LogcatSinceArgs(string? boundary)
        => boundary is null ? null : ["-T", boundary];

    /// <summary>
    /// Starts <c>adb -s serial logcat -v threadtime</c> and streams lines to <paramref name="onLine"/>
    /// until cancelled. Returns when the process has exited.
    /// </summary>
    public async Task StreamLogcatAsync(string serial, Action<string> onLine, CancellationToken ct, IEnumerable<string>? extraArgs = null)
    {
        var args = new List<string> { "-s", serial, "logcat", "-v", "threadtime" };
        args.AddRange(extraArgs ?? []);
        try
        {
            await ProcessRunner.StreamLinesAsync(AdbPath, args, onLine, ct).ConfigureAwait(false);
        }
        catch (ToolException ex) when (ex is not AdbException)
        {
            throw new AdbException("cannot start adb logcat", ex);
        }
    }

    /// <summary>Dump the current logcat buffer filtered by pid (null = all).</summary>
    public async Task<string> LogcatDumpAsync(string serial, int? pid, CancellationToken ct)
    {
        var args = new List<string> { "logcat", "-d" };
        if (pid is not null) { args.Add("--pid"); args.Add(pid.Value.ToString(CultureInfo.InvariantCulture)); }
        var r = await RunDeviceAsync(serial, args, ct).ConfigureAwait(false);
        return r.StdOut;
    }

    /// <summary>Stream logcat lines for a pid until cancelled.</summary>
    public async IAsyncEnumerable<string> LogcatStreamAsync(string serial, int pid, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var line in ProcessRunner.ReadLinesAsync(AdbPath, ["-s", serial, "logcat", "--pid", pid.ToString(CultureInfo.InvariantCulture)], ct).ConfigureAwait(false))
            yield return line;
    }

    // ------------------------------------------------------------------ helpers

    private async Task<AdbResult> RunCoreAsync(IReadOnlyList<string> args, CancellationToken ct, TimeSpan timeout)
    {
        ProcessResult r;
        try
        {
            r = await ProcessRunner.RunAsync(AdbPath, args, ct, timeout).ConfigureAwait(false);
        }
        catch (ToolException ex) when (ex is not AdbException)
        {
            throw new AdbException($"adb {string.Join(' ', args)} {Describe(ex, timeout)}", ex);
        }
        return new AdbResult(r.ExitCode, r.StdOut, r.StdErr);
    }

    /// <summary>What a process-level failure means for an adb call, worded the way callers match on ("timed out").</summary>
    private static string Describe(ToolException ex, TimeSpan timeout)
        => ex.Message.Contains("timed out", StringComparison.Ordinal)
            ? $"timed out after {timeout.TotalSeconds:F0}s"
            : $"could not run: {ex.Message}";
}
