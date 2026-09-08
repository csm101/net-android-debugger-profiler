using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace NetAndroid.Mcp.Tests;

/// <summary>What every test here needs: the built servers, a client over stdio, and the device to use.</summary>
internal static class Support
{
    private const string BuildConfiguration =
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NetAndroidDebuggerProfiler.slnx")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
        }
    }

    public static string ServerDll(string project) =>
        Path.Combine(RepoRoot, "src", project, "bin", BuildConfiguration, "net10.0", project + ".dll");

    public static async Task<McpClient> ConnectAsync(string project, CancellationToken ct)
    {
        var dll = ServerDll(project);
        Assert.True(File.Exists(dll), $"server not built: {dll}");
        var transport = new StdioClientTransport(new StdioClientTransportOptions { Name = project, Command = "dotnet", Arguments = [dll] });
        return await McpClient.CreateAsync(transport, cancellationToken: ct);
    }

    public static async Task<(bool IsError, string Text)> CallAsync(McpClient client, string tool, Dictionary<string, object?> args, CancellationToken ct)
    {
        var result = await client.CallToolAsync(tool, args, cancellationToken: ct);
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        return (result.IsError == true, text);
    }

    /// <summary>The device the suites are pointed at, else the only one online, else null.</summary>
    public static async Task<string?> DeviceSerialAsync(AdbClient adb, CancellationToken ct)
    {
        foreach (var variable in new[] { "NAD_DEVICE_SERIAL", "NAP_TEST_SERIAL" })
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } serial)
                return serial;
        var online = (await adb.ListDevicesAsync(ct)).Where(d => d.State == "device").ToList();
        return online.Count == 1 ? online[0].Serial : null;
    }

    /// <summary>1-based line of the first occurrence of <paramref name="snippet"/> in a source file.</summary>
    public static int LineOf(string sourceFile, string snippet)
    {
        var lines = File.ReadAllLines(sourceFile);
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Contains(snippet, StringComparison.Ordinal))
                return i + 1;
        throw new InvalidOperationException($"'{snippet}' not found in {sourceFile}");
    }
}
