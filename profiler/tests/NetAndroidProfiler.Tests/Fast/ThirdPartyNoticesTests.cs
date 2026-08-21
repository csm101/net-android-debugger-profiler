using System.Text.Json;

namespace NetAndroidProfiler.Tests.Fast;

/// <summary>
/// Shipping a component whose license is not acknowledged is a licensing defect,
/// and dependency closures change silently when a package is added. These tests
/// read what the build actually distributes and check it against
/// THIRD-PARTY-NOTICES.txt.
/// </summary>
public class ThirdPartyNoticesTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NetAndroidProfiler.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string NoticesText() => File.ReadAllText(Path.Combine(RepoRoot(), "THIRD-PARTY-NOTICES.txt"));

    /// <summary>Package ids the given project's build output depends on at run time.</summary>
    private static IReadOnlyList<string> DistributedPackages(string project, string assembly)
    {
        string configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        string deps = Path.Combine(RepoRoot(), "src", project, "bin", configuration, "net10.0", assembly + ".deps.json");
        Assert.True(File.Exists(deps), $"build {project} first: {deps} not found");

        using var doc = JsonDocument.Parse(File.ReadAllText(deps));
        var libraries = doc.RootElement.GetProperty("libraries");
        var packages = new List<string>();
        foreach (var library in libraries.EnumerateObject())
        {
            if (library.Value.GetProperty("type").GetString() != "package") continue;
            packages.Add(library.Name.Split('/')[0]);
        }
        return packages;
    }

    /// <summary>A package is acknowledged by name, or by the family entry that names its license and repository.</summary>
    private static bool IsAcknowledged(string package, string notices) =>
        notices.Contains(package, StringComparison.OrdinalIgnoreCase) ||
        (package.StartsWith("Microsoft.Extensions.", StringComparison.OrdinalIgnoreCase) && notices.Contains("Microsoft.Extensions.*", StringComparison.Ordinal)) ||
        (package.StartsWith("SQLitePCLRaw.", StringComparison.OrdinalIgnoreCase) && notices.Contains("SQLitePCLRaw", StringComparison.Ordinal));

    [Fact]
    public void Every_distributed_package_is_acknowledged()
    {
        string notices = NoticesText();
        var missing = new List<string>();
        foreach (var (project, assembly) in new[]
        {
            ("NetAndroidProfiler.Mcp", "NetAndroidProfiler.Mcp"),
            ("NetAndroidProfiler.Weave", "nap-weave"),
        })
            missing.AddRange(DistributedPackages(project, assembly).Where(p => !IsAcknowledged(p, notices)));

        Assert.True(missing.Count == 0,
            "THIRD-PARTY-NOTICES.txt does not mention: " + string.Join(", ", missing.Distinct().Order()));
    }

    [Fact]
    public void Notices_carry_the_full_license_texts()
    {
        string notices = NoticesText();
        // MIT, twice: the .NET Foundation components and Mono.Cecil's own copyright.
        Assert.Contains("Copyright (c) .NET Foundation and Contributors", notices);
        Assert.Contains("Copyright (c) 2008 - 2015 Jb Evain", notices);
        Assert.Contains("THE SOFTWARE IS PROVIDED \"AS IS\"", notices);
        // Apache-2.0 in full, not just by reference.
        Assert.Contains("Apache License", notices);
        Assert.Contains("Version 2.0, January 2004", notices);
        Assert.Contains("END OF TERMS AND CONDITIONS", notices);
        Assert.Contains("APPENDIX: How to apply the Apache License", notices);
        // The dependency policy forbids copyleft licenses.
        foreach (var forbidden in new[] { "GNU GENERAL PUBLIC LICENSE", "GNU LESSER GENERAL PUBLIC" })
            Assert.DoesNotContain(forbidden, notices, StringComparison.OrdinalIgnoreCase);
    }
}
