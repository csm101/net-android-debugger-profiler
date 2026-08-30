using NetAndroidProfiler.Core.Apps;
using NetAndroidProfiler.Core.Sessions;
using NetAndroidProfiler.Core.Store;

namespace NetAndroidProfiler.Tests.Fast;

/// <summary>
/// Keeping a Get Results and finding it again later, which is what makes the AQTime
/// habit work: clear, exercise the app, archive, and go on profiling without losing what
/// was just measured. An archive is an ordinary result database, so these tests check the
/// two things that makes true - it opens on its own, and it is listed without a session.
/// </summary>
public class ArchiveTests : IDisposable
{
    private readonly string _root;

    public ArchiveTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "net-android-profiler-tests", "archives", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>A session with results but no device: everything here is disk and SQLite.</summary>
    private ProfilerSession SessionWithResults(string package = "com.acme.app")
    {
        var session = ProfilerSession.Create(
            new SessionSpec("emulator-test", package, ProfilingMode.Instrumenting, Engine: InstrumentingEngine.Weaver),
            _root);
        using (var store = ResultStore.Create(session.Info.DatabasePath, ProfilerSession.ToolVersion))
        {
            store.WriteSession(new SessionRow(session.Info.Id, "Instrumenting", "Collecting", package,
                "emulator-test", DateTimeOffset.UtcNow, null, null, null, null, "{}", null));
            store.AddSegment(DateTimeOffset.UtcNow, "snapshot", 1234, "before the archive");
        }
        return session;
    }

    [Fact]
    public async Task An_archive_is_a_result_database_that_opens_on_its_own()
    {
        var session = SessionWithResults();

        var archive = await session.ArchiveAsync("the customers screen");

        Assert.Equal("the customers screen", archive.Name);
        Assert.True(File.Exists(archive.Path));
        // Read back with no session in sight: this is what the GUI does on a click.
        using var store = ResultStore.Open(archive.Path);
        Assert.Equal("com.acme.app", store.ReadSession()!.Package);
        var segments = store.Segments();
        Assert.Single(segments);
        Assert.Equal(1234, segments[0].events);
    }

    [Fact]
    public async Task Archives_are_listed_newest_first_without_a_running_session()
    {
        var session = SessionWithResults();
        await session.ArchiveAsync("first");
        await session.ArchiveAsync("second");

        var listed = ProfilerSession.ListArchives(session.Info.Directory);

        Assert.Equal(["second", "first"], listed.Select(a => a.Name));
        Assert.All(listed, a => Assert.True(File.Exists(a.Path)));
    }

    [Fact]
    public async Task The_same_name_twice_keeps_both_archives()
    {
        var session = SessionWithResults();

        var first = await session.ArchiveAsync("same name");
        var second = await session.ArchiveAsync("same name");

        Assert.NotEqual(first.Path, second.Path);
        Assert.True(File.Exists(first.Path));
        Assert.Equal(2, ProfilerSession.ListArchives(session.Info.Directory).Count);
    }

    [Fact]
    public async Task A_name_that_is_not_a_file_name_still_produces_one_inside_the_session()
    {
        var session = SessionWithResults();

        var archive = await session.ArchiveAsync(@"..\..\etc: the ""slow"" screen ?");

        // The name is the user's; the file name is ours, and it stays where it belongs.
        Assert.StartsWith(Path.GetFullPath(session.Info.Directory), Path.GetFullPath(archive.Path));
        Assert.True(File.Exists(archive.Path));
        Assert.Equal(@"..\..\etc: the ""slow"" screen ?", ProfilerSession.ListArchives(session.Info.Directory)[0].Name);
    }

    [Fact]
    public async Task An_archive_without_a_name_is_still_named_and_dated()
    {
        var session = SessionWithResults();

        var archive = await session.ArchiveAsync(null);

        Assert.False(string.IsNullOrWhiteSpace(archive.Name));
        Assert.True(DateTimeOffset.UtcNow - archive.CreatedUtc < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task An_archive_whose_sidecar_was_lost_is_still_listed_and_openable()
    {
        var session = SessionWithResults();
        var archive = await session.ArchiveAsync("no sidecar");
        File.Delete(Path.ChangeExtension(archive.Path, ".json"));

        var listed = ProfilerSession.ListArchives(session.Info.Directory);

        Assert.Single(listed);
        Assert.Equal("no sidecar", listed[0].Name);      // the file name carries it
        using var store = ResultStore.Open(listed[0].Path);
        Assert.NotNull(store.ReadSession());
    }

    [Fact]
    public async Task A_session_with_no_results_yet_says_so_instead_of_writing_an_empty_archive()
    {
        var session = ProfilerSession.Create(
            new SessionSpec("emulator-test", "com.acme.app", ProfilingMode.Sampling), _root);

        var failure = await Assert.ThrowsAsync<ProfilerException>(() => session.ArchiveAsync("nothing here"));

        Assert.Contains("no results", failure.Message);
        Assert.Empty(ProfilerSession.ListArchives(session.Info.Directory));
    }

    /// <summary>
    /// The registry is what every frontend asks for results, so an archive has to be
    /// openable through it: otherwise a kept Get Results could be listed and never read.
    /// </summary>
    [Fact]
    public async Task An_archive_is_read_back_through_the_registry_by_its_path()
    {
        var session = SessionWithResults();
        var archive = await session.ArchiveAsync("read me back");

        await using var registry = new SessionRegistry(_root);
        using var store = registry.OpenResults(archive.Path, out string id);

        Assert.Equal(archive.Path, id);
        Assert.Equal("com.acme.app", store.ReadSession()!.Package);
        Assert.Contains("No result database", Assert.Throws<ProfilerException>(
            () => registry.OpenResults(Path.Combine(_root, "gone.db"), out _)).Message);
    }

    [Fact]
    public void A_session_that_never_archived_anything_lists_nothing()
    {
        var session = SessionWithResults();
        Assert.Empty(ProfilerSession.ListArchives(session.Info.Directory));
        Assert.Empty(ProfilerSession.ListArchives(Path.Combine(_root, "not-a-session")));
    }
}
