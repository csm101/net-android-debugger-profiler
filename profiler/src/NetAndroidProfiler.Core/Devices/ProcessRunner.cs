using System.Diagnostics;
using System.Text;

namespace NetAndroidProfiler.Core.Devices;

/// <summary>Result of a finished external process.</summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Thrown when an external tool fails.</summary>
public sealed class ToolException : Exception
{
    public ToolException(string message) : base(message) { }
    public ToolException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Runs external tools (adb, dsrouter) and captures their output.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null, byte[]? stdin = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
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
        if (stdin is not null)
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

    /// <summary>Run and throw <see cref="ToolException"/> on a non-zero exit code.</summary>
    public static async Task<ProcessResult> RunCheckedAsync(string fileName, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null, byte[]? stdin = null)
    {
        var r = await RunAsync(fileName, args, ct, timeout, stdin).ConfigureAwait(false);
        if (!r.Success)
            throw new ToolException($"'{Path.GetFileName(fileName)} {string.Join(' ', args)}' failed ({r.ExitCode}): {Trim(r.StdErr)} {Trim(r.StdOut)}".Trim());
        return r;
    }

    private static string Trim(string s) => s.Length > 600 ? s[..600] + "..." : s.Trim();
}
