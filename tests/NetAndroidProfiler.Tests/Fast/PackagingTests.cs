using System.Reflection;
using NetAndroidProfiler.Core.Devices;
using NetAndroidProfiler.Core.Sessions;

namespace NetAndroidProfiler.Tests.Fast;

/// <summary>
/// What a release is made of: one version across the product, and the tools the profiler
/// drives taken from the package when it carries them. Both are silent when they break -
/// a stale version stamp in a database, or a globally installed dsrouter quietly used
/// instead of the one that shipped.
/// </summary>
public class PackagingTests
{
    [Fact]
    public void The_tool_version_comes_from_the_build_not_from_a_literal()
    {
        string assemblyVersion = typeof(ProfilerSession).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

        Assert.Equal(assemblyVersion, ProfilerSession.ToolVersion);
        Assert.NotEqual("0.0.0", ProfilerSession.ToolVersion);
        // Directory.Build.props is the single place a release is bumped.
        Assert.Matches(@"^\d+\.\d+\.\d+", ProfilerSession.ToolVersion);
    }

    [Fact]
    public void A_packaged_tool_wins_over_the_globally_installed_one()
    {
        string package = Path.Combine(Path.GetTempPath(), "nap-package-" + Guid.NewGuid().ToString("N"));
        string tools = Path.Combine(package, "tools");
        Directory.CreateDirectory(tools);
        string exe = Path.Combine(tools, OperatingSystem.IsWindows() ? "dotnet-dsrouter.exe" : "dotnet-dsrouter");
        File.WriteAllText(exe, "");
        try
        {
            Assert.Equal(exe, ToolLocator.FindDotnetTool("dotnet-dsrouter", package));
        }
        finally
        {
            Directory.Delete(package, recursive: true);
        }
    }

    /// <summary>The package puts bin/ and tools/ side by side, so bin/ has to look up.</summary>
    [Fact]
    public void The_tool_is_found_next_to_the_bin_directory_too()
    {
        string package = Path.Combine(Path.GetTempPath(), "nap-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(package, "tools"));
        Directory.CreateDirectory(Path.Combine(package, "bin"));
        string exe = Path.Combine(package, "tools", OperatingSystem.IsWindows() ? "dotnet-dsrouter.exe" : "dotnet-dsrouter");
        File.WriteAllText(exe, "");
        try
        {
            Assert.Equal(exe, ToolLocator.FindDotnetTool("dotnet-dsrouter", Path.Combine(package, "bin")));
        }
        finally
        {
            Directory.Delete(package, recursive: true);
        }
    }

    [Fact]
    public void Without_a_packaged_copy_the_search_falls_through()
    {
        string empty = Path.Combine(Path.GetTempPath(), "nap-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            // Whatever this machine has - a global tool, PATH, or nothing - the result must
            // not come from the empty package directory.
            string? found = ToolLocator.FindDotnetTool("dotnet-dsrouter", empty);
            Assert.True(found is null || !found.StartsWith(empty, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }
}
