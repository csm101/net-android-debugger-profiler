using NetAndroidProfiler.Core.Projects;
using NetAndroidProfiler.Core.Sessions;

namespace NetAndroidProfiler.Tests.Fast;

/// <summary>
/// Reading the sources instead of asking the user to retype them: which projects of a
/// solution can be profiled, what package they install, where their build output is, and
/// which assemblies belong to the app. No device, no build - only project files.
/// </summary>
public class AppProjectFinderTests : IDisposable
{
    private readonly string _root;

    public AppProjectFinderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "net-android-profiler-tests", "projects", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------ fixtures

    private string Project(string name, string content)
    {
        string folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name + ".csproj");
        File.WriteAllText(path, content);
        return path;
    }

    private string AndroidApp(string name = "Acme.Droid", string applicationId = "com.acme.app", string? extra = null) =>
        Project(name, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0-android35.0</TargetFramework>
                <OutputType>Exe</OutputType>
                <ApplicationId>{applicationId}</ApplicationId>
                {extra}
              </PropertyGroup>
            </Project>
            """);

    private string AndroidLibrary(string name = "Acme.Core") =>
        Project(name, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0-android35.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

    private string Solution(string name, params string[] projects)
    {
        string path = Path.Combine(_root, name + ".slnx");
        string entries = string.Join('\n', projects.Select(p => $"""  <Project Path="{Path.GetRelativePath(_root, p)}" />"""));
        File.WriteAllText(path, $"<Solution>\n{entries}\n</Solution>\n");
        return path;
    }

    // ------------------------------------------------------------------ tests

    [Fact]
    public void A_solution_offers_the_applications_and_not_the_libraries()
    {
        string app = AndroidApp();
        string library = AndroidLibrary();
        string desktop = Project("Acme.Tools", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup>
            </Project>
            """);
        string solution = Solution("Acme", app, library, desktop);

        var found = AppProjectFinder.Find(solution);

        Assert.Single(found);
        Assert.Equal(app, found[0].ProjectPath);
        Assert.Equal("com.acme.app", found[0].ApplicationId);
        Assert.Equal("net9.0-android35.0", found[0].TargetFramework);
        Assert.True(found[0].InSolution);
    }

    [Fact]
    public void The_package_can_come_from_the_manifest_when_the_project_does_not_declare_it()
    {
        string project = Project("Legacy.Droid", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0-android35.0</TargetFramework>
                <OutputType>Exe</OutputType>
              </PropertyGroup>
            </Project>
            """);
        string properties = Path.Combine(Path.GetDirectoryName(project)!, "Properties");
        Directory.CreateDirectory(properties);
        File.WriteAllText(Path.Combine(properties, "AndroidManifest.xml"),
            """<manifest xmlns:android="http://schemas.android.com/apk/res/android" package="com.legacy.droid" />""");

        var found = AppProjectFinder.Describe(project);

        Assert.NotNull(found);
        Assert.Equal("com.legacy.droid", found!.ApplicationId);
    }

    [Fact]
    public void The_build_output_follows_the_configuration_that_was_asked_for()
    {
        string app = AndroidApp();

        var debug = AppProjectFinder.Describe(app);
        var profiling = AppProjectFinder.Describe(app, "Profiling");

        Assert.EndsWith(Path.Combine("bin", "Debug", "net9.0-android35.0"), debug!.OutputDir);
        Assert.EndsWith(Path.Combine("bin", "Profiling", "net9.0-android35.0"), profiling!.OutputDir);
        Assert.False(debug.OutputExists);          // nothing was built
    }

    [Fact]
    public void The_assemblies_to_weave_include_the_referenced_projects()
    {
        string library = AndroidLibrary();
        string app = AndroidApp(extra: null);
        File.WriteAllText(app, File.ReadAllText(app).Replace("</Project>",
            $"""
              <ItemGroup>
                <ProjectReference Include="..\Acme.Core\Acme.Core.csproj" />
              </ItemGroup>
            </Project>
            """));

        var found = AppProjectFinder.Describe(app)!;

        Assert.Equal(["Acme.Droid", "Acme.Core"], found.Assemblies);
        Assert.True(File.Exists(library));
    }

    [Fact]
    public void The_properties_a_profiling_build_needs_are_reported_as_declared()
    {
        string app = AndroidApp(extra: "<EnableDiagnostics>true</EnableDiagnostics><EmbedAssembliesIntoApk>true</EmbedAssembliesIntoApk>");
        string plain = AndroidApp("Other.Droid", "com.other.app");

        Assert.True(AppProjectFinder.Describe(app)!.EnableDiagnostics);
        Assert.True(AppProjectFinder.Describe(app)!.EmbedAssembliesIntoApk);
        Assert.Null(AppProjectFinder.Describe(plain)!.EnableDiagnostics);
        Assert.Null(AppProjectFinder.Describe(plain)!.EmbedAssembliesIntoApk);
    }

    [Fact]
    public void Walking_a_folder_ignores_the_copies_under_bin_and_obj()
    {
        string app = AndroidApp();
        string buried = Path.Combine(Path.GetDirectoryName(app)!, "obj", "Debug");
        Directory.CreateDirectory(buried);
        File.Copy(app, Path.Combine(buried, "Acme.Droid.csproj"));

        var found = AppProjectFinder.Find(_root);

        Assert.Single(found);
        Assert.Equal(app, found[0].ProjectPath);
    }

    [Fact]
    public void A_path_that_holds_nothing_profilable_answers_an_empty_list()
    {
        AndroidLibrary();
        Assert.Empty(AppProjectFinder.Find(_root));
    }

    [Fact]
    public void A_path_that_is_neither_solution_nor_project_says_so()
    {
        string stray = Path.Combine(_root, "notes.txt");
        File.WriteAllText(stray, "hello");

        Assert.Contains("neither a solution nor a .csproj", Assert.Throws<ProfilerException>(() => AppProjectFinder.Find(stray)).Message);
        Assert.Contains("No such solution", Assert.Throws<ProfilerException>(() => AppProjectFinder.Find(Path.Combine(_root, "nowhere"))).Message);
    }

    [Fact]
    public void Callspec_candidates_come_from_the_assemblies_that_were_built()
    {
        // This test assembly is the only build output a fast test can count on.
        var candidates = AppProjectFinder.Candidates(AppContext.BaseDirectory, ["NetAndroidProfiler.Tests"]);

        Assert.Contains(candidates, c => c.Callspec == "N:NetAndroidProfiler.Tests.Fast" && c.Kind == "namespace");
        Assert.Contains(candidates, c => c.Callspec == "T:" + typeof(AppProjectFinderTests).FullName && c.Kind == "type");
        Assert.All(candidates, c => Assert.True(c.Methods > 0));
        // Each candidate names the assembly it came out of: that, and not the whole app,
        // is what a weaving session has to rewrite.
        Assert.All(candidates, c => Assert.Equal("NetAndroidProfiler.Tests", c.Assembly));
    }

    [Fact]
    public void An_app_that_was_never_built_offers_no_candidates()
    {
        var found = AppProjectFinder.Describe(AndroidApp())!;
        Assert.Empty(AppProjectFinder.Candidates(found.OutputDir, found.Assemblies));
    }
}
