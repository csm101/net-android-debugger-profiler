using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace NetAndroid.Device;

/// <summary>Result of a finished external process.</summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Result of a finished external process whose stdout is binary.</summary>
public sealed record ProcessBytesResult(int ExitCode, byte[] StdOut, string StdErr);

/// <summary>Thrown when an external tool fails.</summary>
public class ToolException : Exception
{
    public ToolException(string message) : base(message) { }
    public ToolException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Runs external tools (adb, dsrouter, dotnet) and captures their output. The single way both
/// products start a process: every child gets its stdin redirected, see <see cref="RunAsync"/>.
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null, byte[]? stdin = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Always redirect stdin, even with nothing to send: a child started without it
            // inherits the parent's, and adb shell and dsrouter both read stdin. In a
            // frontend that speaks over stdio - the MCP server - the child then eats the
            // client's requests and the server goes silent without an error anywhere.
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        try { p.Start(); }
        catch (Exception e) { throw new ToolException($"Cannot start '{fileName}': {e.Message}", e); }

        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        if (stdin is null)
        {
            // Nothing to send: close it at once, so the child reads end-of-file instead of
            // waiting on a pipe nobody writes to.
            p.StandardInput.Close();
        }
        else
        {
            await p.StandardInput.BaseStream.WriteAsync(stdin, ct).ConfigureAwait(false);
            p.StandardInput.Close();
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is not null) cts.CancelAfter(timeout.Value);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            if (ct.IsCancellationRequested) throw;
            throw new ToolException($"'{fileName} {string.Join(' ', args)}' timed out after {timeout}");
        }
        return new ProcessResult(p.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    /// <summary>
    /// Runs a tool whose output is worth watching while it works - a build takes minutes -
    /// handing every line to <paramref name="onLine"/> as it arrives, and answers the exit
    /// code. A non-zero code is not an exception here: the output is the diagnosis.
    /// </summary>
    public static async Task<int> RunStreamingAsync(
        string fileName, IReadOnlyList<string> args, Action<string> onLine, CancellationToken ct, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,          // see RunAsync: a child must never inherit our stdin
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var done = new TaskCompletionSource();
        int pending = 2;
        void Line(object _, DataReceivedEventArgs e)
        {
            if (e.Data is null)
            {
                if (Interlocked.Decrement(ref pending) == 0) done.TrySetResult();
                return;
            }
            try { onLine(e.Data); } catch { /* a frontend's logging must not kill the build */ }
        }
        p.OutputDataReceived += Line;
        p.ErrorDataReceived += Line;

        try { p.Start(); }
        catch (Exception e) { throw new ToolException($"Cannot start '{fileName}': {e.Message}", e); }
        p.StandardInput.Close();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is not null) cts.CancelAfter(timeout.Value);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            await done.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException) { /* the pipes did not close; the exit code is still the answer */ }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            if (ct.IsCancellationRequested) throw;
            throw new ToolException($"'{fileName} {string.Join(' ', args)}' timed out after {timeout}");
        }
        return p.ExitCode;
    }

    /// <summary>Run and throw <see cref="ToolException"/> on a non-zero exit code.</summary>
    public static async Task<ProcessResult> RunCheckedAsync(string fileName, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null, byte[]? stdin = null)
    {
        var r = await RunAsync(fileName, args, ct, timeout, stdin).ConfigureAwait(false);
        if (!r.Success)
            throw new ToolException($"'{Path.GetFileName(fileName)} {string.Join(' ', args)}' failed ({r.ExitCode}): {Trim(r.StdErr)} {Trim(r.StdOut)}".Trim());
        return r;
    }

    /// <summary>
    /// Runs a tool whose stdout is bytes, not text (<c>adb exec-out</c>): stdout is copied to the
    /// end before the exit is awaited, so nothing is left in the pipe. Same stdin rule as
    /// <see cref="RunAsync"/>.
    /// </summary>
    public static async Task<ProcessBytesResult> RunBytesAsync(string fileName, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,          // see RunAsync: a child must never inherit our stdin
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        try { p.Start(); }
        catch (Exception e) { throw new ToolException($"Cannot start '{fileName}': {e.Message}", e); }
        p.StandardInput.Close();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is not null) cts.CancelAfter(timeout.Value);
        var stdout = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(stdout, 1 << 16, cts.Token);
        var stderr = p.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await copy.ConfigureAwait(false);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            if (ct.IsCancellationRequested) throw;
            throw new ToolException($"'{fileName} {string.Join(' ', args)}' timed out after {timeout}");
        }
        return new ProcessBytesResult(p.ExitCode, stdout.ToArray(), await stderr.ConfigureAwait(false));
    }

    /// <summary>
    /// Runs a tool that never ends on its own (<c>adb logcat</c>) and hands every stdout line to
    /// <paramref name="onLine"/> as it arrives. Returns when the process exits or when
    /// <paramref name="ct"/> is cancelled, which kills it; cancellation is the normal way to stop
    /// and is not reported as an error.
    /// </summary>
    public static async Task StreamLinesAsync(string fileName, IReadOnlyList<string> args, Action<string> onLine, CancellationToken ct)
    {
        await foreach (var line in ReadLinesAsync(fileName, args, ct).ConfigureAwait(false))
            onLine(line);
    }

    /// <summary>
    /// The stdout lines of a tool that never ends on its own, as they arrive; the enumeration ends
    /// when the process exits or the token is cancelled, which kills it.
    /// </summary>
    public static async IAsyncEnumerable<string> ReadLinesAsync(string fileName, IReadOnlyList<string> args, [EnumeratorCancellation] CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,          // see RunAsync: a child must never inherit our stdin
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process p;
        try { p = Process.Start(psi) ?? throw new ToolException($"Cannot start '{fileName}'"); }
        catch (Exception e) when (e is not ToolException) { throw new ToolException($"Cannot start '{fileName}': {e.Message}", e); }
        using (p)
        {
            p.StandardInput.Close();
            using var reg = ct.Register(() => { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best effort */ } });
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    string? line;
                    try { line = await p.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { yield break; }
                    if (line is null) yield break;
                    yield return line;
                }
            }
            finally
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best effort */ }
            }
        }
    }

    private static string Trim(string s) => s.Length > 600 ? s[..600] + "..." : s.Trim();
}
