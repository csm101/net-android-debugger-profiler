using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NetAndroidDebugger.Core.Adb;

/// <summary>Result of one adb invocation.</summary>
public sealed record AdbResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Thrown when an adb command fails.</summary>
public sealed class AdbException(string message) : Exception(message);

/// <summary>
/// Thin wrapper over the adb executable. Every device-bound call takes an explicit
/// serial: the engine never relies on the adb default device (other emulators or
/// devices may be attached).
/// </summary>
public sealed class AdbClient(string adbPath = "adb")
{
    private static readonly Regex DeviceLine = new(@"^(?<serial>\S+)\s+(?<state>\S+)(?<rest>.*)$", RegexOptions.Compiled);

    public string AdbPath { get; } = adbPath;

    /// <summary>Runs <c>adb [args]</c> (no serial) and returns the result without throwing on non-zero exit.</summary>
    public Task<AdbResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
        => RunCoreAsync(args, ct, timeout ?? TimeSpan.FromSeconds(60));

    /// <summary>Runs <c>adb -s serial [args]</c>; throws <see cref="AdbException"/> on non-zero exit.</summary>
    public async Task<AdbResult> RunDeviceAsync(string serial, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
    {
        var full = new List<string>(args.Count + 2) { "-s", serial };
        full.AddRange(args);
        var r = await RunCoreAsync(full, ct, timeout ?? TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        if (!r.Succeeded)
            throw new AdbException($"adb -s {serial} {string.Join(' ', args)} failed ({r.ExitCode}): {r.StdErr.Trim()} {r.StdOut.Trim()}".Trim());
        return r;
    }

    /// <summary>
    /// Runs <c>adb -s serial [args]</c> and returns raw stdout bytes (for <c>exec-out</c>, whose
    /// output is binary and must not go through text decoding). Throws on non-zero exit.
    /// </summary>
    public async Task<byte[]> RunDeviceBytesAsync(string serial, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(AdbPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add(serial);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new AdbException($"cannot start {AdbPath}");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var effective = timeout ?? TimeSpan.FromSeconds(60);
        cts.CancelAfter(effective);
        var stdout = new MemoryStream();
        var copyTask = proc.StandardOutput.BaseStream.CopyToAsync(stdout, cts.Token);
        var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            await copyTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            if (ct.IsCancellationRequested) throw;
            throw new AdbException($"adb -s {serial} {string.Join(' ', args)} timed out after {effective.TotalSeconds:F0}s");
        }
        var stderr = await stderrTask.ConfigureAwait(false);
        if (proc.ExitCode != 0)
            throw new AdbException($"adb -s {serial} {string.Join(' ', args)} failed ({proc.ExitCode}): {stderr.Trim()}");
        return stdout.ToArray();
    }

    /// <summary>Runs <c>adb -s serial shell command</c> and returns stdout.</summary>
    public async Task<string> ShellAsync(string serial, string command, CancellationToken ct, TimeSpan? timeout = null)
    {
        var r = await RunDeviceAsync(serial, ["shell", command], ct, timeout).ConfigureAwait(false);
        return r.StdOut;
    }

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
            var rest = m.Groups["rest"].Value;
            var model = Regex.Match(rest, @"model:(\S+)") is { Success: true } mm ? mm.Groups[1].Value : null;
            list.Add(new DeviceInfo(serial, m.Groups["state"].Value, model, serial.StartsWith("emulator-", StringComparison.Ordinal)));
        }
        return list;
    }

    public Task ForwardAsync(string serial, int hostPort, int devicePort, CancellationToken ct)
        => RunDeviceAsync(serial, ["forward", $"tcp:{hostPort}", $"tcp:{devicePort}"], ct);

    public async Task RemoveForwardAsync(string serial, int hostPort, CancellationToken ct)
    {
        // Not fatal when the forward no longer exists.
        await RunCoreAsync(["-s", serial, "forward", "--remove", $"tcp:{hostPort}"], ct, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
    }

    public Task SetPropAsync(string serial, string name, string value, CancellationToken ct)
        // An empty value needs the quotes to survive `adb shell` argument joining.
        => ShellAsync(serial, value.Length == 0 ? $"setprop {name} ''" : $"setprop {name} '{value}'", ct);

    public async Task<string> GetPropAsync(string serial, string name, CancellationToken ct)
        => (await ShellAsync(serial, $"getprop {name}", ct).ConfigureAwait(false)).Trim();

    /// <summary>Unix epoch seconds according to the device clock (the freshness deadline must use it, not the host clock).</summary>
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
    /// share the sandbox this debugger drives.
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

    public Task LogcatClearAsync(string serial, CancellationToken ct)
        => RunDeviceAsync(serial, ["logcat", "-c"], ct);

    private static readonly Regex LogcatStamp = new(@"^(?<md>\d\d-\d\d) (?<time>\d\d:\d\d:\d\d\.\d\d\d)", RegexOptions.Compiled);

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
        var psi = new ProcessStartInfo(AdbPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add(serial);
        psi.ArgumentList.Add("logcat");
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("threadtime");
        foreach (var a in extraArgs ?? [])
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new AdbException("cannot start adb logcat");
        using var reg = ct.Register(() => { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* best effort */ } });
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await proc.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                onLine(line);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }
    }

    private async Task<AdbResult> RunCoreAsync(IReadOnlyList<string> args, CancellationToken ct, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(AdbPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new AdbException($"cannot start {AdbPath}");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            if (ct.IsCancellationRequested) throw;
            throw new AdbException($"adb {string.Join(' ', args)} timed out after {timeout.TotalSeconds:F0}s");
        }
        return new AdbResult(proc.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }
}
