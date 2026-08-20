using System.Diagnostics;
using NetAndroidProfiler.Core.Devices;

namespace NetAndroidProfiler.Core.Collection;

/// <summary>
/// Owns one dotnet-dsrouter process: IPC server (dotnet-diagnostic-dsrouter-&lt;pid&gt;)
/// bridged to a TCP server the Android runtime connects to. Emulators use
/// <c>android-emu</c> (host 127.0.0.1:9000, app connects to 10.0.2.2:9000);
/// physical devices use <c>android</c> plus <c>adb reverse tcp:9000 tcp:9001</c>
/// (app connects to 127.0.0.1:9000). dsrouter 9.0.x has no port option.
/// </summary>
public sealed class DsRouterProcess : IAsyncDisposable
{
    public const int AppPort = 9000;           // port the app connects to
    public const int DeviceHostPort = 9001;    // host port of "dsrouter android" behind adb reverse

    private readonly Process _process;
    private readonly List<string> _log = new();

    private DsRouterProcess(Process p, bool isEmulator) { _process = p; IsEmulator = isEmulator; }

    public int Pid => _process.Id;
    public bool IsEmulator { get; }
    public bool HasExited => _process.HasExited;
    public IReadOnlyList<string> Log { get { lock (_log) return _log.ToList(); } }

    /// <summary>Address the app must connect to (for DOTNET_DiagnosticPorts / debug.mono.profile).</summary>
    public string AppAddress => IsEmulator ? $"10.0.2.2:{AppPort}" : $"127.0.0.1:{AppPort}";

    public static async Task<DsRouterProcess> StartAsync(bool isEmulator, CancellationToken ct, string? dsrouterPath = null)
    {
        string exe = dsrouterPath ?? ToolLocator.FindDsRouter()
            ?? throw new ToolException("dotnet-dsrouter not found: dotnet tool install -g dotnet-dsrouter");
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(isEmulator ? "android-emu" : "android");
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var router = new DsRouterProcess(p, isEmulator);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        p.OutputDataReceived += (_, e) => router.OnLine(e.Data, started);
        p.ErrorDataReceived += (_, e) => router.OnLine(e.Data, started);
        p.Exited += (_, _) => started.TrySetResult(false);
        if (!p.Start()) throw new ToolException("dotnet-dsrouter failed to start");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var reg = ct.Register(() => started.TrySetCanceled(ct));
        var timeout = Task.Delay(TimeSpan.FromSeconds(20), ct);
        var done = await Task.WhenAny(started.Task, timeout).ConfigureAwait(false);
        if (done != started.Task || !await started.Task.ConfigureAwait(false))
        {
            await router.DisposeAsync().ConfigureAwait(false);
            throw new ToolException("dotnet-dsrouter did not start its IPC server: " + string.Join(" | ", router.Log.TakeLast(5)));
        }
        return router;
    }

    private void OnLine(string? line, TaskCompletionSource<bool> started)
    {
        if (line is null) return;
        lock (_log) { _log.Add(line); if (_log.Count > 500) _log.RemoveAt(0); }
        if (line.Contains("Starting IPC server", StringComparison.Ordinal) || line.Contains("<--> TCP server", StringComparison.Ordinal))
            started.TrySetResult(true);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        catch { /* best effort */ }
        _process.Dispose();
    }
}
