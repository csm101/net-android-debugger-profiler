using System.Diagnostics;
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

    /// <summary>Processes whose name is <paramref name="package"/> or starts with <c>package:</c> (helper processes).</summary>
    public async Task<IReadOnlyList<(int Pid, string Name)>> ListPackageProcessesAsync(string serial, string package, CancellationToken ct)
    {
        var outp = await ShellAsync(serial, "ps -A -o PID,NAME", ct).ConfigureAwait(false);
        var result = new List<(int, string)>();
        foreach (var raw in outp.Split('\n'))
        {
            var parts = raw.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var pid)) continue;
            var name = parts[1];
            if (name == package || name.StartsWith(package + ":", StringComparison.Ordinal))
                result.Add((pid, name));
        }
        return result;
    }

    public Task LogcatClearAsync(string serial, CancellationToken ct)
        => RunDeviceAsync(serial, ["logcat", "-c"], ct);

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
