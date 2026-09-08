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
    private static async Task<List<string>> ToolNamesAsync(string project, CancellationToken ct)
    {
        await using var client = await Support.ConnectAsync(project, ct);
        return (await client.ListToolsAsync(cancellationToken: ct)).Select(t => t.Name).ToList();
    }

    [Fact]
    public async Task Handshake_NamesTheUnifiedServer()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await using var client = await Support.ConnectAsync("NetAndroid.Mcp", cts.Token);
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
        await using var client = await Support.ConnectAsync("NetAndroid.Mcp", cts.Token);
        var result = await Support.CallAsync(client, "list_app_projects",
            new() { ["path"] = Path.Combine(Support.RepoRoot, "TestTarget", "Profiler") }, cts.Token);
        Assert.False(result.IsError, result.Text);
        Assert.Contains("com.mcasoftware.testtarget", result.Text);
        Assert.Contains("symbolsDir", result.Text);
    }

    [Fact]
    public async Task GetAppOutput_WithoutADebugSession_AsksForDeviceAndPackage()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await using var client = await Support.ConnectAsync("NetAndroid.Mcp", cts.Token);
        var result = await Support.CallAsync(client, "get_app_output", new(), cts.Token);
        Assert.True(result.IsError, result.Text);
        Assert.Contains("deviceSerial", result.Text);
        Assert.Contains("packageName", result.Text);
    }

    [Fact]
    public void Ships_NoAssembly_TheTwoProductServersDoNot()
    {
        var unified = Path.GetDirectoryName(Support.ServerDll("NetAndroid.Mcp"))!;
        var products = new[] { "NetAndroidDebugger.Mcp", "NetAndroidProfiler.Mcp" }
            .Select(p => Path.GetDirectoryName(Support.ServerDll(p))!)
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
