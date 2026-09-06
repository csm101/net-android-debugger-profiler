using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NetAndroid.Mcp;

namespace NetAndroid.Mcp.Tests;

/// <summary>
/// The unified server as a process over stdio, next to the two product servers it is made of.
/// No device: what is checked is the handshake, the tool list and the two shared tools that
/// answer without one.
/// </summary>
public sealed class UnifiedServerTests
{
    private const string BuildConfiguration =
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NetAndroidDebuggerProfiler.slnx")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
        }
    }

    private static string ServerDll(string project) =>
        Path.Combine(RepoRoot, "src", project, "bin", BuildConfiguration, "net10.0", project + ".dll");

    private static async Task<McpClient> ConnectAsync(string project, CancellationToken ct)
    {
        var dll = ServerDll(project);
        Assert.True(File.Exists(dll), $"server not built: {dll}");
        var transport = new StdioClientTransport(new StdioClientTransportOptions { Name = project, Command = "dotnet", Arguments = [dll] });
        return await McpClient.CreateAsync(transport, cancellationToken: ct);
    }

    private static async Task<List<string>> ToolNamesAsync(string project, CancellationToken ct)
    {
        await using var client = await ConnectAsync(project, ct);
        return (await client.ListToolsAsync(cancellationToken: ct)).Select(t => t.Name).ToList();
    }

    [Fact]
    public async Task Handshake_NamesTheUnifiedServer()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await using var client = await ConnectAsync("NetAndroid.Mcp", cts.Token);
        Assert.Equal(UnifiedServer.Name, client.ServerInfo.Name);
        Assert.Contains("profile_run", client.ServerInstructions ?? "");
        Assert.Contains("launch_app", client.ServerInstructions ?? "");
    }

    [Fact]
    public async Task ToolList_IsTheUnionOfBothProductServers_WithTheSharedThreeOnce()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var unified = await ToolNamesAsync("NetAndroid.Mcp", cts.Token);
        var debugger = await ToolNamesAsync("NetAndroidDebugger.Mcp", cts.Token);
        var profiler = await ToolNamesAsync("NetAndroidProfiler.Mcp", cts.Token);

        Assert.Equal(SharedTools.Names.Order(), debugger.Intersect(profiler).Order());
        Assert.Equal(debugger.Union(profiler).Order(), unified.Order());
        Assert.Equal(unified.Count, unified.Distinct().Count());
    }

    [Fact]
    public async Task ListAppProjects_ReadsTheProfilerTestTarget()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await using var client = await ConnectAsync("NetAndroid.Mcp", cts.Token);
        var result = await client.CallToolAsync("list_app_projects",
            new Dictionary<string, object?> { ["path"] = Path.Combine(RepoRoot, "TestTarget", "Profiler") }, cancellationToken: cts.Token);
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.False(result.IsError == true, text);
        Assert.Contains("com.mcasoftware.testtarget", text);
        Assert.Contains("symbolsDir", text);
    }

    [Fact]
    public async Task GetAppOutput_WithoutADebugSession_AsksForDeviceAndPackage()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await using var client = await ConnectAsync("NetAndroid.Mcp", cts.Token);
        var result = await client.CallToolAsync("get_app_output", new Dictionary<string, object?>(), cancellationToken: cts.Token);
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.True(result.IsError == true, text);
        Assert.Contains("deviceSerial", text);
        Assert.Contains("packageName", text);
    }

    [Fact]
    public void Ships_NoAssembly_TheTwoProductServersDoNot()
    {
        var unified = Path.GetDirectoryName(ServerDll("NetAndroid.Mcp"))!;
        var products = new[] { "NetAndroidDebugger.Mcp", "NetAndroidProfiler.Mcp" }
            .Select(p => Path.GetDirectoryName(ServerDll(p))!)
            .SelectMany(d => Directory.GetFiles(d, "*.dll"))
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extra = Directory.GetFiles(unified, "*.dll")
            .Select(Path.GetFileName)
            .Where(name => !name!.StartsWith("NetAndroid.Mcp", StringComparison.Ordinal) && !products.Contains(name!))
            .ToList();
        // THIRD-PARTY-NOTICES.txt is checked by the two product suites against what each server ships;
        // this server ships their union and nothing more, so their checks cover it.
        Assert.Empty(extra);
    }
}
