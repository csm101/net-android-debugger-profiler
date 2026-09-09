using NetAndroidProfiler.Core;
using NetAndroidProfiler.Core.Apps;
using NetAndroidProfiler.Core.Sessions;

namespace NetAndroidProfiler.Tests.Fast;

/// <summary>
/// Sessions as things somebody keeps: named, thrown away, and told apart by the product
/// they belong to. All of it is disk and session.json - no device, no database.
/// </summary>
public class SessionCatalogTests : IDisposable
{
    private readonly string _root;

    public SessionCatalogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "net-android-profiler-tests", "catalog", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private ProfilerSession NewSession(string package = "com.acme.app", string? name = null, string? solution = null) =>
        ProfilerSession.Create(
            new SessionSpec("emulator-test", package, ProfilingMode.Sampling, Name: name, SolutionPath: solution),
            _root);

    [Fact]
    public void A_session_records_the_solution_it_came_from()
    {
        var session = NewSession(solution: @"C:\work\Acme\Acme.sln");

        var listed = ProfilerSession.ListSessions(_root).Single(s => s.id == session.Id);

        Assert.Equal(@"C:\work\Acme\Acme.sln", listed.spec!.SolutionPath);
    }

    /// <summary>A session written before the spec carried a solution still reads.</summary>
    [Fact]
    public void A_session_without_a_solution_reads_as_one_without()
    {
        var session = NewSession();

        var listed = ProfilerSession.ListSessions(_root).Single(s => s.id == session.Id);

        Assert.Null(listed.spec!.SolutionPath);
        Assert.Null(listed.spec.ProjectPath);
    }

    [Fact]
    public void Renaming_a_session_changes_what_it_is_listed_by_and_not_its_id()
    {
        var session = NewSession();

        new SessionRegistry(_root).Rename(session.Id, "  the slow startup  ");

        var listed = ProfilerSession.ListSessions(_root).Single(s => s.id == session.Id);
        Assert.Equal("the slow startup", listed.spec!.Name);
        Assert.Equal(session.Id, listed.id);
    }

    [Fact]
    public void A_name_can_be_taken_away()
    {
        var session = NewSession(name: "first try");

        new SessionRegistry(_root).Rename(session.Id, "   ");

        Assert.Null(ProfilerSession.ListSessions(_root).Single(s => s.id == session.Id).spec!.Name);
    }

    [Fact]
    public void Deleting_a_session_takes_everything_it_recorded()
    {
        var session = NewSession();
        File.WriteAllText(Path.Combine(session.Directory, "session.db"), "not really a database");
        Directory.CreateDirectory(Path.Combine(session.Directory, "archives"));

        new SessionRegistry(_root).Delete(session.Id);

        Assert.False(Directory.Exists(session.Directory));
        Assert.Empty(ProfilerSession.ListSessions(_root));
    }

    /// <summary>
    /// A session directory somewhere else is reached by its path: sessions are not all
    /// kept together once the frontend can choose where to put one.
    /// </summary>
    [Fact]
    public void A_session_outside_the_root_is_named_by_its_path()
    {
        string elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var session = ProfilerSession.Create(new SessionSpec("emulator-test", "com.acme.app", ProfilingMode.Sampling), elsewhere);

        new SessionRegistry(Path.Combine(_root, "sessions")).Rename(session.Directory, "kept beside the product");

        Assert.Equal("kept beside the product", ProfilerSession.ListSessions(elsewhere).Single().spec!.Name);
    }

    /// <summary>
    /// Starting paused and suspending at launch are opposites: suspending holds the app
    /// until a diagnostic session connects, which is the very thing being deferred.
    /// </summary>
    [Fact]
    public void A_session_that_records_on_demand_does_not_suspend_the_app_at_launch()
    {
        var spec = SessionSpecFactory.Build("emulator-test", "com.acme.app", suspendOnStart: true, startPaused: true);

        Assert.True(spec.StartPaused);
        Assert.False(spec.SuspendOnStart);
    }

    [Fact]
    public async Task Starting_to_record_is_refused_on_a_session_that_was_never_paused()
    {
        var session = NewSession();

        var e = await Assert.ThrowsAsync<ProfilerException>(() => session.StartRecordingAsync());

        Assert.Contains("not started paused", e.Message);
    }

    /// <summary>
    /// The symbols are what let results be shown next to the source, and they can only be
    /// read from the build they belong to: when nobody passes them, they are looked for.
    /// </summary>
    [Fact]
    public void The_build_output_of_the_project_is_taken_as_the_symbols_directory()
    {
        string output = Path.Combine(_root, "bin", "Debug", "net9.0-android35.0");
        Directory.CreateDirectory(output);
        string project = Path.Combine(_root, "Acme.Droid.csproj");
        File.WriteAllText(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0-android35.0</TargetFramework>
                <OutputType>Exe</OutputType>
                <ApplicationId>com.acme.app</ApplicationId>
              </PropertyGroup>
            </Project>
            """);

        var spec = SessionSpecFactory.Build("emulator-test", "com.acme.app", projectPath: project);

        Assert.Equal(output, spec.SymbolsDir);
        Assert.Equal(project, spec.ProjectPath);
    }

    [Fact]
    public void An_unknown_session_says_so_instead_of_throwing_an_io_error()
    {
        var registry = new SessionRegistry(_root);

        var e = Assert.Throws<ProfilerException>(() => registry.Delete("20990101-000000-nobody-sampling"));

        Assert.Contains("Unknown session", e.Message);
    }
}
