using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetAndroid.Mcp.Tests;

/// <summary>
/// The plugin under <c>plugin/</c>: the skill that tells an agent how to use the server, and
/// the registration files. What these tests hold: the skill names only tools the server has
/// and covers the ones that start and end its engines; it stays generic (no product, no
/// machine, no repository); the plugin files parse and point at the server the package ships.
/// </summary>
public sealed class SkillTests
{
    private static string PluginRoot => Path.Combine(Support.RepoRoot, "plugin");
    private static string SkillDir => Path.Combine(PluginRoot, "skills", "net-android");
    private static string SkillFile => Path.Combine(SkillDir, "SKILL.md");

    private static IEnumerable<string> SkillFiles() =>
        Directory.GetFiles(SkillDir, "*.md", SearchOption.AllDirectories);

    private static IEnumerable<string> PluginTextFiles() =>
        Directory.GetFiles(PluginRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

    /// <summary>Backticked snake_case tokens: the shape of a tool name in the skill's prose.</summary>
    private static readonly Regex ToolShaped = new(@"`([a-z]+(?:_[a-z]+)+)`", RegexOptions.Compiled);

    /// <summary>Backticked tokens that look like tools but are parameters or values named in the same style.</summary>
    private static readonly HashSet<string> NotTools = new(StringComparer.Ordinal)
    {
        "incl_cpu", "excl_cpu", "login_button", "increment_button",
    };

    [Fact]
    public void The_skill_has_a_name_and_a_description_and_stays_short()
    {
        var lines = File.ReadAllLines(SkillFile);
        Assert.Equal("---", lines[0]);
        var end = Array.IndexOf(lines, "---", 1);
        Assert.True(end > 0, "frontmatter not closed");
        var frontmatter = lines[1..end];
        Assert.Contains(frontmatter, l => l.StartsWith("name: net-android", StringComparison.Ordinal));
        var description = frontmatter.Single(l => l.StartsWith("description:", StringComparison.Ordinal));
        Assert.InRange(description.Length, 80, 1536 + "description: ".Length);
        Assert.InRange(lines.Length, 100, 400);
    }

    [Fact]
    public async Task Every_tool_the_skill_names_exists_in_the_server()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await using var client = await Support.ConnectAsync("NetAndroid.Mcp", cts.Token);
        var tools = (await client.ListToolsAsync(cancellationToken: cts.Token)).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        var unknown = new List<string>();
        foreach (var file in SkillFiles())
            foreach (Match m in ToolShaped.Matches(File.ReadAllText(file)))
            {
                var name = m.Groups[1].Value;
                if (!tools.Contains(name) && !NotTools.Contains(name))
                    unknown.Add($"{Path.GetFileName(file)}: {name}");
            }
        Assert.True(unknown.Count == 0, "named in the skill, missing in the server:\n" + string.Join("\n", unknown.Distinct()));
    }

    [Fact]
    public void The_tools_that_start_and_end_an_engine_are_all_covered()
    {
        var text = string.Concat(SkillFiles().Select(File.ReadAllText));
        foreach (var tool in new[]
                 {
                     "list_devices", "check_device_control", "list_app_projects", "check_app", "build_app",
                     "profile_run", "profile_start", "profile_stop", "profile_hotspots", "profile_tree", "profile_timings",
                     "alloc_report", "heap_diff", "profile_annotate_source", "profile_report",
                     "launch_app", "launch_from_config", "stop_debugging", "set_breakpoint", "remove_all_breakpoints",
                     "set_exception_rules", "continue_and_wait", "wait_until_stopped", "get_locals",
                     "get_ui_hierarchy", "capture_screenshot", "tap_screen", "type_text", "press_key", "wake_screen",
                 })
            Assert.Contains($"`{tool}`", text);
    }

    [Fact]
    public void The_plugin_names_no_product_machine_or_repository()
    {
        // The skill is for anyone with a .NET for Android app: nothing of the apps it was
        // developed against, the company's tooling, or the machine it was written on.
        var forbidden = new[]
        {
            "the reference application", "App.Droid", "App", "GitLab", "MCA-", "C:\\Athens", "C:\\Work", "X:\\Temp",
            "emulator-5554", "emulator-5556", "TestTarget", "ProfileMeExample", "com.mcasoftware",
            "net.androiddebugger", "register-mcp", "NAD_", "NAP_TEST", "Redmi", "MIUI",
        };
        var hits = new List<string>();
        foreach (var file in PluginTextFiles())
        {
            var text = File.ReadAllText(file);
            foreach (var word in forbidden)
                if (text.Contains(word, StringComparison.OrdinalIgnoreCase))
                    hits.Add($"{Path.GetRelativePath(PluginRoot, file)}: {word}");
        }
        Assert.True(hits.Count == 0, string.Join("\n", hits));
    }

    [Fact]
    public void Every_reference_the_skill_links_exists()
    {
        var links = Regex.Matches(File.ReadAllText(SkillFile), @"\]\(([^)]+\.md)\)").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(links);
        foreach (var link in links)
            Assert.True(File.Exists(Path.Combine(SkillDir, link)), "dangling link: " + link);
        foreach (var reference in Directory.GetFiles(Path.Combine(SkillDir, "references"), "*.md"))
            Assert.Contains("references/" + Path.GetFileName(reference), links);
    }

    [Fact]
    public void The_plugin_registers_the_unified_server_the_package_ships()
    {
        using var plugin = JsonDocument.Parse(File.ReadAllText(Path.Combine(PluginRoot, ".claude-plugin", "plugin.json")));
        Assert.Equal(UnifiedServer.Name, plugin.RootElement.GetProperty("name").GetString());
        var server = plugin.RootElement.GetProperty("mcpServers").GetProperty(UnifiedServer.Name);
        Assert.Equal("dotnet", server.GetProperty("command").GetString());
        var dll = server.GetProperty("args")[0].GetString()!;
        Assert.StartsWith("${CLAUDE_PLUGIN_ROOT}/bin/", dll);
        Assert.EndsWith(Path.GetFileName(Support.ServerDll("NetAndroid.Mcp")), dll);

        using var marketplace = JsonDocument.Parse(File.ReadAllText(Path.Combine(PluginRoot, ".claude-plugin", "marketplace.json")));
        var entry = marketplace.RootElement.GetProperty("plugins").EnumerateArray().Single();
        Assert.Equal(UnifiedServer.Name, entry.GetProperty("name").GetString());
        Assert.Equal("./", entry.GetProperty("source").GetString());
    }
}
