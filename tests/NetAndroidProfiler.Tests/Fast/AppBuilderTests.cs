using NetAndroidProfiler.Core.Projects;
using NetAndroidProfiler.Core.Sessions;

namespace NetAndroidProfiler.Tests.Fast;

/// <summary>
/// The build the GUI can run for you: the command line must carry exactly the properties
/// a profiling session needs, or the session that follows fails on a device for reasons
/// that have nothing to do with the device.
/// </summary>
public class AppBuilderTests : IDisposable
{
    private readonly string _root;
    private readonly string _project;

    public AppBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "net-android-profiler-tests", "builder", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _project = Path.Combine(_root, "Acme.Droid.csproj");
        File.WriteAllText(_project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0-android35.0</TargetFramework><ApplicationId>com.acme.app</ApplicationId></PropertyGroup>
            </Project>
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void The_default_build_installs_a_diagnostics_enabled_fast_deployment_debug_build()
    {
        var args = AppBuilder.ArgumentsFor(new AppBuildRequest(_project, DeviceSerial: "emulator-5556"));

        Assert.Equal(["build", _project, "-c", "Debug"], args.Take(4));
        Assert.Contains("-t:Install", args);
        Assert.Contains("-p:EnableDiagnostics=true", args);
        Assert.Contains("-p:EmbedAssembliesIntoApk=false", args);
        // One argument, spaces included: msbuild wants AdbTarget to be the whole adb flag.
        Assert.Contains("-p:AdbTarget=-s emulator-5556", args);
    }

    [Fact]
    public void Nothing_is_added_that_was_not_asked_for()
    {
        var args = AppBuilder.ArgumentsFor(new AppBuildRequest(
            _project, "Release", DeviceSerial: null, EnableDiagnostics: false, FastDeployment: false, Install: false));

        Assert.Contains("Release", args);
        Assert.DoesNotContain("-t:Install", args);
        Assert.DoesNotContain(args, a => a.StartsWith("-p:EnableDiagnostics"));
        Assert.DoesNotContain(args, a => a.StartsWith("-p:AdbTarget"));
        // The deployment layout is the exception: it is always stated, because a choice
        // that leaves the project's default in place is not a choice - see
        // Keeping_the_assemblies_in_the_apk_is_said_explicitly.
        Assert.Contains("-p:EmbedAssembliesIntoApk=true", args);
    }

    [Fact]
    public void Clearing_the_deployed_assemblies_is_an_adb_step_and_never_an_msbuild_property()
    {
        var args = AppBuilder.ArgumentsFor(new AppBuildRequest(
            _project, DeviceSerial: "emulator-5556", ClearDeployedAssemblies: true, PackageName: "com.acme.app"));

        // It happens before the build, through run-as; msbuild knows nothing about it.
        Assert.DoesNotContain(args, a => a.Contains("Clear", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(args, a => a.Contains("__override__"));
        Assert.DoesNotContain(args, a => a.Contains("com.acme.app"));
    }

    [Fact]
    public void Keeping_the_assemblies_in_the_apk_is_said_explicitly()
    {
        // Not "leave the property alone": a project whose Debug default is fast deployment
        // would otherwise ignore the choice.
        var args = AppBuilder.ArgumentsFor(new AppBuildRequest(_project, FastDeployment: false));
        Assert.Contains("-p:EmbedAssembliesIntoApk=true", args);
        Assert.DoesNotContain("-p:EmbedAssembliesIntoApk=false", args);
    }

    [Fact]
    public void Weaving_during_the_build_passes_the_targets_and_the_callspec()
    {
        string targets = Path.Combine(_root, "NetAndroidProfiler.Weaving.targets");
        File.WriteAllText(targets, "<Project />");

        var args = AppBuilder.ArgumentsFor(new AppBuildRequest(
            _project, FastDeployment: false, Weave: true,
            Callspec: "T:Acme.Sync.SyncService,T:Acme.Sync.Other", WeavingTargets: targets));

        Assert.Contains("-p:NapWeave=true", args);
        // A comma separates properties on an msbuild command line; a callspec may hold them.
        Assert.Contains("-p:NapCallspec=T:Acme.Sync.SyncService%2CT:Acme.Sync.Other", args);
        Assert.Contains(args, a => a.StartsWith("-p:CustomAfterMicrosoftCommonTargets=") && a.EndsWith(targets));
    }

    [Fact]
    public void The_libraries_named_are_woven_too_and_not_only_the_app()
    {
        // An app is more than its own assembly: the business logic usually lives in a
        // library, and a callspec pointing at one instruments nothing unless the build
        // is told to weave it as well. the reference application is exactly that shape.
        string targets = Path.Combine(_root, "NetAndroidProfiler.Weaving.targets");
        File.WriteAllText(targets, "<Project />");

        var args = AppBuilder.ArgumentsFor(new AppBuildRequest(
            _project, Weave: true, Callspec: "N:V7", WeavingTargets: targets,
            WeaveAssemblies: ["App.Core", "App.GeoLocation"]));

        // Escaped, because msbuild splits properties on a semicolon: unescaped, the second name
        // arrives as another property and the build is refused with "invalid property".
        Assert.Contains("-p:NapAssemblies=App.Core%3BApp.GeoLocation", args);
        Assert.DoesNotContain(args, a => a.StartsWith("-p:NapAssemblies=") && a.Contains(';'));
    }

    /// <summary>
    /// The targets default the weaver and the collector to the package's build\tools, which a
    /// source tree does not have until the packaging step writes it. Where the binaries are is
    /// this side's question - it is the side that knows - so the build is told, and a clone that
    /// was merely built weaves without anybody being sent to run a publish first.
    /// </summary>
    [Fact]
    public void The_build_is_told_where_the_weaver_and_the_collector_are()
    {
        string targets = Path.Combine(_root, "NetAndroidProfiler.Weaving.targets");
        File.WriteAllText(targets, "<Project />");

        var args = AppBuilder.ArgumentsFor(new AppBuildRequest(
            _project, Weave: true, Callspec: "N:V7", WeavingTargets: targets));

        var weaver = args.FirstOrDefault(a => a.StartsWith("-p:NapWeaveTool="));
        var collector = args.FirstOrDefault(a => a.StartsWith("-p:NapCollectorAssembly="));
        Assert.NotNull(weaver);
        Assert.NotNull(collector);
        Assert.True(File.Exists(weaver!["-p:NapWeaveTool=".Length..]), weaver);
        Assert.True(File.Exists(collector!["-p:NapCollectorAssembly=".Length..]), collector);
    }
    [Fact]
    public void Weaving_during_the_build_without_a_callspec_is_refused()
    {
        string targets = Path.Combine(_root, "NetAndroidProfiler.Weaving.targets");
        File.WriteAllText(targets, "<Project />");

        var failure = Assert.Throws<ProfilerException>(() => AppBuilder.ArgumentsFor(
            new AppBuildRequest(_project, Weave: true, WeavingTargets: targets)));

        Assert.Contains("callspec", failure.Message);
    }

    [Fact]
    public void A_build_that_does_not_weave_says_nothing_about_weaving()
    {
        var args = AppBuilder.ArgumentsFor(new AppBuildRequest(_project));
        Assert.DoesNotContain(args, a => a.Contains("NapWeave") || a.Contains("NapCallspec")
            || a.Contains("CustomAfterMicrosoftCommonTargets"));
    }

    [Fact]
    public void A_project_that_does_not_exist_is_refused_before_msbuild_is_started()
    {
        var missing = new AppBuildRequest(Path.Combine(_root, "Nope.csproj"));
        Assert.Contains("No such project", Assert.Throws<ProfilerException>(() => AppBuilder.ArgumentsFor(missing)).Message);
        Assert.Throws<ProfilerException>(() => AppBuilder.ArgumentsFor(new AppBuildRequest("")));
    }
}
