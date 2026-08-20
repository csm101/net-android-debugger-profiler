using System.Runtime.CompilerServices;
using System.Text;

namespace NetAndroidProfiler.Core.Devices;

/// <summary>An attached Android device or emulator.</summary>
public sealed record DeviceInfo(string Serial, string State, bool IsEmulator, string? Model, string? AvdName, int ApiLevel, string Abi);

/// <summary>
/// Serial-explicit adb wrapper. Every call targets one device; there is no
/// "default device" (two emulators may be attached - see ANDROID_PROFILING_NOTES).
/// </summary>
public sealed class AdbClient
{
    public AdbClient(string? adbPath = null)
    {
        AdbPath = adbPath ?? ToolLocator.FindAdb() ?? throw new ToolException("adb not found: pass its path or add platform-tools to PATH");
    }

    public string AdbPath { get; }

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public async Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken ct)
    {
        var r = await ProcessRunner.RunCheckedAsync(AdbPath, ["devices"], ct, DefaultTimeout).ConfigureAwait(false);
        var list = new List<DeviceInfo>();
        foreach (var line in r.StdOut.Split('\n').Skip(1))
        {
            var parts = line.Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            string serial = parts[0], state = parts[1];
            if (state != "device") { list.Add(new DeviceInfo(serial, state, serial.StartsWith("emulator-"), null, null, 0, "")); continue; }
            string model = await GetPropAsync(serial, "ro.product.model", ct).ConfigureAwait(false);
            string sdk = await GetPropAsync(serial, "ro.build.version.sdk", ct).ConfigureAwait(false);
            string abi = await GetPropAsync(serial, "ro.product.cpu.abi", ct).ConfigureAwait(false);
            string? avd = null;
            if (serial.StartsWith("emulator-"))
            {
                var a = await ProcessRunner.RunAsync(AdbPath, ["-s", serial, "emu", "avd", "name"], ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                avd = a.StdOut.Split('\n').FirstOrDefault()?.Trim();
                if (string.IsNullOrEmpty(avd) || avd.StartsWith("OK")) avd = null;
            }
            list.Add(new DeviceInfo(serial, state, serial.StartsWith("emulator-"), model, avd, int.TryParse(sdk, out var api) ? api : 0, abi));
        }
        return list;
    }

    public Task<ProcessResult> RunAsync(string serial, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null) =>
        ProcessRunner.RunAsync(AdbPath, ["-s", serial, .. args], ct, timeout ?? DefaultTimeout);

    public Task<ProcessResult> RunCheckedAsync(string serial, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null) =>
        ProcessRunner.RunCheckedAsync(AdbPath, ["-s", serial, .. args], ct, timeout ?? DefaultTimeout);

    /// <summary>adb shell; returns stdout (trimmed). The command string is passed to the device shell as-is.</summary>
    public async Task<string> ShellAsync(string serial, string command, CancellationToken ct, TimeSpan? timeout = null)
    {
        var r = await RunCheckedAsync(serial, ["shell", command], ct, timeout).ConfigureAwait(false);
        return r.StdOut.Trim();
    }

    /// <summary>adb exec-out: binary-safe stdout of a device command.</summary>
    public async Task<byte[]> ExecOutAsync(string serial, string command, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(AdbPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-s", serial, "exec-out", command }) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new ToolException("cannot start adb");
        using var ms = new MemoryStream();
        var err = p.StandardError.ReadToEndAsync(ct);
        // Drain stdout to EOF *before* waiting for exit: waiting first can leave data
        // in the pipe and truncate the payload (observed: 2 KB of a 6.5 KB assembly).
        await p.StandardOutput.BaseStream.CopyToAsync(ms, 1 << 16, ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        if (p.ExitCode != 0) throw new ToolException($"adb exec-out '{command}' failed: {await err.ConfigureAwait(false)}");
        return ms.ToArray();
    }

    public Task<string> GetPropAsync(string serial, string name, CancellationToken ct) => ShellAsync(serial, $"getprop {name}", ct);

    /// <summary>setprop; an empty value clears the property.</summary>
    public Task SetPropAsync(string serial, string name, string value, CancellationToken ct) =>
        ShellAsync(serial, $"setprop {name} '{value}'", ct);

    public Task PushAsync(string serial, string localPath, string remotePath, CancellationToken ct) =>
        RunCheckedAsync(serial, ["push", localPath, remotePath], ct, TimeSpan.FromMinutes(5));

    public Task PullAsync(string serial, string remotePath, string localPath, CancellationToken ct) =>
        RunCheckedAsync(serial, ["pull", remotePath, localPath], ct, TimeSpan.FromMinutes(10));

    /// <summary>Run a command inside the app sandbox (debuggable apps only). Returns stdout; throws on failure.</summary>
    public async Task<string> RunAsAsync(string serial, string package, string command, CancellationToken ct)
    {
        var r = await RunAsync(serial, ["shell", $"run-as {package} sh -c '{command.Replace("'", "'\\''")}'"], ct).ConfigureAwait(false);
        if (!r.Success) throw new ToolException($"run-as {package} '{command}' failed: {r.StdErr.Trim()} {r.StdOut.Trim()}".Trim());
        return r.StdOut.Trim();
    }

    /// <summary>True when <c>run-as</c> works for the package, i.e. the app is debuggable.</summary>
    public async Task<bool> IsDebuggableAsync(string serial, string package, CancellationToken ct)
    {
        var r = await RunAsync(serial, ["shell", $"run-as {package} id"], ct).ConfigureAwait(false);
        return r.Success && r.StdOut.Contains("uid=");
    }

    /// <summary>Paths of the installed APK(s) of a package (base + splits), empty when not installed.</summary>
    public async Task<IReadOnlyList<string>> PackagePathsAsync(string serial, string package, CancellationToken ct)
    {
        var r = await RunAsync(serial, ["shell", $"pm path {package}"], ct).ConfigureAwait(false);
        return r.StdOut.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("package:")).Select(l => l["package:".Length..]).ToList();
    }

    public async Task<int?> PidOfAsync(string serial, string package, CancellationToken ct)
    {
        var r = await RunAsync(serial, ["shell", $"pidof {package}"], ct).ConfigureAwait(false);
        var first = r.StdOut.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return int.TryParse(first, out int pid) ? pid : null;
    }

    public Task ForceStopAsync(string serial, string package, CancellationToken ct) => ShellAsync(serial, $"am force-stop {package}", ct);

    /// <summary>
    /// Launch the package's LAUNCHER activity. Resolves the activity name and
    /// uses an explicit `am start` (monkey's LAUNCHER intent sometimes reuses a
    /// dead task record after force-stop cycles and never spawns the process -
    /// observed with App.Droid); falls back to monkey when resolution fails.
    /// </summary>
    public async Task LaunchAsync(string serial, string package, CancellationToken ct)
    {
        var resolve = await RunAsync(serial, ["shell", $"cmd package resolve-activity --brief -c android.intent.category.LAUNCHER {package}"], ct).ConfigureAwait(false);
        string? component = resolve.StdOut.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(package + "/", StringComparison.Ordinal));
        if (component is not null)
        {
            var start = await RunCheckedAsync(serial, ["shell", $"am start -W -n {component}"], ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (start.StdOut.Contains("Error", StringComparison.OrdinalIgnoreCase))
                throw new ToolException($"am start {component} failed on {serial}: {start.StdOut.Trim()}");
            return;
        }
        var r = await RunCheckedAsync(serial, ["shell", $"monkey -p {package} -c android.intent.category.LAUNCHER 1"], ct).ConfigureAwait(false);
        if (r.StdOut.Contains("No activities found", StringComparison.OrdinalIgnoreCase))
            throw new ToolException($"Package {package} has no LAUNCHER activity (or is not installed) on {serial}");
    }

    public Task ReverseAsync(string serial, int devicePort, int hostPort, CancellationToken ct) =>
        RunCheckedAsync(serial, ["reverse", $"tcp:{devicePort}", $"tcp:{hostPort}"], ct);

    public Task ReverseRemoveAsync(string serial, int devicePort, CancellationToken ct) =>
        RunAsync(serial, ["reverse", "--remove", $"tcp:{devicePort}"], ct);

    public Task LogcatClearAsync(string serial, CancellationToken ct) => RunAsync(serial, ["logcat", "-c"], ct);

    /// <summary>Dump the current logcat buffer filtered by pid (null = all).</summary>
    public async Task<string> LogcatDumpAsync(string serial, int? pid, CancellationToken ct)
    {
        var args = new List<string> { "logcat", "-d" };
        if (pid is not null) { args.Add("--pid"); args.Add(pid.Value.ToString()); }
        var r = await RunCheckedAsync(serial, args, ct).ConfigureAwait(false);
        return r.StdOut;
    }

    /// <summary>Stream logcat lines for a pid until cancelled.</summary>
    public async IAsyncEnumerable<string> LogcatStreamAsync(string serial, int pid, [EnumeratorCancellation] CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(AdbPath) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in new[] { "-s", serial, "logcat", "--pid", pid.ToString() }) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new ToolException("cannot start adb logcat");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await p.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) yield break;
                yield return line;
            }
        }
        finally
        {
            try { if (!p.HasExited) p.Kill(); } catch { }
        }
    }
}
