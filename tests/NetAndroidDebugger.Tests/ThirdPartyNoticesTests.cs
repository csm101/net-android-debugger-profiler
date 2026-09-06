using NetAndroidDebugger.Tests.Harness;

namespace NetAndroidDebugger.Tests;

/// <summary>
/// THIRD-PARTY-NOTICES.txt has to describe what is actually shipped. A dependency that arrives
/// without a notice is a licence breach at the moment the product is distributed, and nothing else
/// in the build would say so: the reference is transitive, the assembly appears in the output
/// folder, and everything keeps working.
/// </summary>
public sealed class ThirdPartyNoticesTests
{
    private const string BuildConfiguration =
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    private static string NoticesPath => Path.Combine(TestEnvironment.RepoRoot, "THIRD-PARTY-NOTICES.txt");

    /// <summary>Every assembly the frontends ship, ours excluded (this product's and the shared device library), must be named in the notices.</summary>
    [Fact]
    public void EveryShippedAssembly_IsNamedInTheNotices()
    {
        var notices = File.ReadAllText(NoticesPath);

        var shipped = new[] { "NetAndroidDebugger.Mcp", "NetAndroidDebugger.Dap" }
            .Select(p => Path.Combine(TestEnvironment.RepoRoot, "src", p, "bin", BuildConfiguration, "net10.0"))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d, "*.dll"))
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => !name.StartsWith("NetAndroidDebugger.", StringComparison.Ordinal)
                        && !name.StartsWith("NetAndroid.Device", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(shipped.Count > 0, "no build output found to check the notices against; build the solution first");

        var missing = shipped.Where(name => !notices.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.True(missing.Count == 0,
            "these assemblies are shipped but not named in THIRD-PARTY-NOTICES.txt: " + string.Join(", ", missing));
    }

    /// <summary>
    /// The licences the notices promise to reproduce must actually be in the file. Referring to a
    /// text that is not there is the same failure as not naming a component.
    /// </summary>
    [Fact]
    public void TheLicenceTextsReferredTo_AreReproducedInFull()
    {
        var notices = File.ReadAllText(NoticesPath);

        // MIT, by its two operative sentences rather than its title.
        Assert.Contains("Permission is hereby granted, free of charge", notices, StringComparison.Ordinal);
        Assert.Contains("THE SOFTWARE IS PROVIDED \"AS IS\"", notices, StringComparison.Ordinal);

        // Apache-2.0 in full: the header alone (which is what most packages carry) is not the licence.
        Assert.Contains("Apache License", notices, StringComparison.Ordinal);
        Assert.Contains("TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION", notices, StringComparison.Ordinal);
        Assert.Contains("END OF TERMS AND CONDITIONS", notices, StringComparison.Ordinal);
        // Section 4 is the one that carries the redistribution duties this file discharges.
        Assert.Contains("4. Redistribution.", notices, StringComparison.Ordinal);
    }
}
