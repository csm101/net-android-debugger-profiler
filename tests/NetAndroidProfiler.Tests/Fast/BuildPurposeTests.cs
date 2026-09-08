using NetAndroidProfiler.Core.Projects;
using NetAndroidProfiler.Core.Sessions;

namespace NetAndroidProfiler.Tests.Fast;

/// <summary>
/// The word a frontend says (build_app's purpose) and the build it stands for. The command
/// lines themselves are AppBuilderTests' business; this is the mapping.
/// </summary>
public sealed class BuildPurposeTests
{
    private static string AnyProject => Path.Combine(RepoRoot(), "TestTarget", "Profiler", "TestTarget.csproj");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NetAndroidProfiler.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
    }

    [Theory]
    [InlineData("debug")]
    [InlineData("sampling")]
    [InlineData("heap")]
    [InlineData("instrumenting")]
    public void Every_on_device_purpose_is_the_same_debug_build_with_diagnostics_and_fast_deployment(string purpose)
    {
        var request = BuildPurpose.ToRequest(purpose, AnyProject, "emulator-5554");
        Assert.Equal("Debug", request.Configuration);
        Assert.True(request.EnableDiagnostics);
        Assert.True(request.FastDeployment);
        Assert.True(request.Install);
        Assert.False(request.Weave);
        Assert.Equal("emulator-5554", request.DeviceSerial);
    }

    [Fact]
    public void Build_time_instrumenting_weaves_with_the_callspec_and_keeps_the_assemblies_embedded()
    {
        var request = BuildPurpose.ToRequest("instrumenting-build-time", AnyProject, callspec: "N:My.App", weaveAssemblies: ["My.App", "My.Core"]);
        Assert.True(request.Weave);
        Assert.False(request.FastDeployment);
        Assert.Equal("N:My.App", request.Callspec);
        Assert.Equal(["My.App", "My.Core"], request.WeaveAssemblies);
        var args = AppBuilder.ArgumentsFor(request);
        Assert.Contains("-p:NapWeave=true", args);
        Assert.Contains("-p:EmbedAssembliesIntoApk=true", args);
    }

    [Fact]
    public void Build_time_instrumenting_without_a_callspec_is_refused_with_the_reason()
    {
        var e = Assert.Throws<ProfilerException>(() => BuildPurpose.ToRequest("instrumenting-build-time", AnyProject));
        Assert.Contains("callspec", e.Message);
    }

    [Fact]
    public void An_unknown_purpose_is_refused_listing_the_known_ones()
    {
        var e = Assert.Throws<ProfilerException>(() => BuildPurpose.ToRequest("release", AnyProject));
        foreach (var known in BuildPurpose.All)
            Assert.Contains(known, e.Message);
    }

    [Fact]
    public void The_purpose_is_case_insensitive_and_the_configuration_can_be_overridden()
    {
        var request = BuildPurpose.ToRequest(" Sampling ", AnyProject, configuration: "Profiling", install: false, clearDeployedAssemblies: true, packageName: "com.example.app");
        Assert.Equal("Profiling", request.Configuration);
        Assert.False(request.Install);
        Assert.True(request.ClearDeployedAssemblies);
        Assert.Equal("com.example.app", request.PackageName);
    }
}
