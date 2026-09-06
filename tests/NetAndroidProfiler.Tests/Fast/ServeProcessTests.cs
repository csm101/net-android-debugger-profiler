using System.Diagnostics;

namespace NetAndroidProfiler.Tests.Fast;

/// <summary>
/// `nap serve` as a process, which is how the GUI uses it: it must report its port on
/// stdout and it must not outlive the frontend that started it. A GUI that is killed
/// rather than closed never gets to say /shutdown, and a service left listening is a
/// process nobody will ever look for.
/// </summary>
public class ServeProcessTests
{
    private static string NapExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NetAndroidProfiler.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        string exe = Path.Combine(dir!.FullName, "src", "NetAndroidProfiler.Cli", "bin", configuration, "net10.0",
            OperatingSystem.IsWindows() ? "nap.exe" : "nap");
        Assert.True(File.Exists(exe), $"nap was not built at {exe}");
        return exe;
    }

    private static Process Start(string fileName, params string[] args)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }

    [Fact]
    public async Task Serve_ends_itself_when_the_process_that_owns_it_exits()
    {
        if (!OperatingSystem.IsWindows()) return;              // the stand-in parent is cmd.exe

        // A parent that stays alive until it is killed: cmd waits on the stdin we redirect.
        using var parent = Start("cmd.exe", "/c", "pause");
        using var serve = Start(NapExe(), "serve", "--parent-pid", parent.Id.ToString());

        // The port line is the handshake the GUI waits for; getting it proves the service
        // is listening rather than already dead.
        var portLine = serve.StandardOutput.ReadLineAsync();
        Assert.Same(portLine, await Task.WhenAny(portLine, Task.Delay(TimeSpan.FromSeconds(30))));
        Assert.Contains("\"port\"", portLine.Result);
        Assert.False(serve.HasExited);

        parent.Kill(entireProcessTree: true);
        try
        {
            await serve.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        }
        catch (OperationCanceledException)
        {
            serve.Kill(entireProcessTree: true);
            Assert.Fail("nap serve outlived the process that owns it.");
        }
    }
}
