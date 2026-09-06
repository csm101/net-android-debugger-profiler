using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
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
    private string? _fatal;

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
            // dsrouter outlives the call that started it and reads stdin. Without this it
            // inherits the parent's, and in the MCP server that is the client's request
            // stream: requests then vanish into dsrouter and the server answers nothing.
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // dsrouter has no port option, so a second one cannot run, and the one already
        // holding the port may be a leftover of an interrupted session. Say that here
        // instead of letting the session fail much later with nothing ever connecting.
        if (IsPortInUse(AppPort))
            throw new ToolException(
                $"Port {AppPort} is already in use, so dotnet-dsrouter cannot start. Another profiling session is running, " +
                "or a dotnet-dsrouter left over from an interrupted one is still alive - stop it and retry.");

        psi.ArgumentList.Add(isEmulator ? "android-emu" : "android");
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("debug");
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var router = new DsRouterProcess(p, isEmulator);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        p.OutputDataReceived += (_, e) => router.OnLine(e.Data, started);
        p.ErrorDataReceived += (_, e) => router.OnLine(e.Data, started);
        p.Exited += (_, _) => started.TrySetResult(false);
        if (!p.Start()) throw new ToolException("dotnet-dsrouter failed to start");
        // Give it end-of-file rather than a pipe nobody writes to: it has nothing to say
        // to us, and a router waiting on input is a router that never reports its port.
        try { p.StandardInput.Close(); } catch { /* it may have exited already */ }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            using var reg = ct.Register(() => started.TrySetCanceled(ct));
            var timeout = Task.Delay(TimeSpan.FromSeconds(20), CancellationToken.None);
            var done = await Task.WhenAny(started.Task, timeout).ConfigureAwait(false);
            if (done != started.Task || !await started.Task.ConfigureAwait(false))
                throw new ToolException("dotnet-dsrouter did not start its IPC server: " +
                                        (router._fatal ?? string.Join(" | ", router.Log.TakeLast(5))));
        }
        catch
        {
            // Cancellation included: the process is already running here and would otherwise
            // keep the port for every later session.
            await router.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return router;
    }

    private void OnLine(string? line, TaskCompletionSource<bool> started)
    {
        if (line is null) return;
        lock (_log) { _log.Add(line); if (_log.Count > 500) _log.RemoveAt(0); }
        // A dsrouter that cannot bind its port reports the error and then prints
        // "Stopping IPC server (...) <--> TCP server (127.0.0.1:9000) router.": matching
        // "<--> TCP server" takes that dying router for a healthy one, and the session then
        // fails much later, with nothing ever connecting to it.
        if (line.Contains("Shutting down due to error", StringComparison.Ordinal))
        {
            _fatal = line.Trim();
            started.TrySetResult(false);
            return;
        }
        if (line.Contains("Starting IPC server", StringComparison.Ordinal))
            started.TrySetResult(true);
    }

    /// <summary>True when something already listens on <paramref name="port"/>.</summary>
    public static bool IsPortInUse(int port)
    {
        foreach (var endpoint in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            if (endpoint.Port == port &&
                (endpoint.Address.Equals(IPAddress.Loopback) || endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any)))
                return true;
        return false;
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
