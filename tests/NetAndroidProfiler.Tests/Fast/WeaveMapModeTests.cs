using NetAndroidProfiler.Core.Sessions;
using NetAndroidProfiler.Core.Weaving;

namespace NetAndroidProfiler.Tests.Fast;

/// <summary>
/// A build-time weave bakes into the app how it records - a call tree kept in memory, or an
/// event per call - and a session cannot change that afterwards. So the map says which, and
/// the session follows it. Found in the field on the reference application: the session asked for the tree,
/// the app had been built to write events, and the results stayed empty with nothing said.
/// </summary>
public class WeaveMapModeTests : IDisposable
{
    private readonly string _root;

    public WeaveMapModeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "net-android-profiler-tests", "map-mode", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string Map(string name) => Path.Combine(_root, name);

    [Fact]
    public void The_map_records_the_mode_the_app_was_built_with()
    {
        var weaver = new CecilWeaver(WeaveFilter.Parse("T:WeaveSample.Shapes"), 1);
        weaver.Weave(Path.Combine(AppContext.BaseDirectory, "WeaveSample.dll"), Path.Combine(_root, "woven.dll"));

        string tree = Map("tree.map");
        string trace = Map("trace.map");
        weaver.WriteMap(tree, "tree");
        weaver.WriteMap(trace, "trace");

        Assert.Equal("tree", CecilWeaver.ReadMapMode(tree));
        Assert.Equal("trace", CecilWeaver.ReadMapMode(trace));
        // The methods are still readable: the header carries the mode without disturbing them.
        Assert.NotEmpty(CecilWeaver.ReadMap(tree));
        Assert.Equal(CecilWeaver.ReadMap(tree).Count, CecilWeaver.ReadMap(trace).Count);
    }

    [Fact]
    public void A_map_from_before_the_mode_was_recorded_reads_as_events()
    {
        // Apps built by an older toolchain have no NAP_PROFILER_MODE baked in, and the
        // collector then writes events: reading such a map as "tree" would leave a session
        // waiting for files that app never writes.
        string old = Map("old.map");
        File.WriteAllLines(old,
        [
            "#napw-map\t1",
            "1\tWeaveSample.dll\t0x06000001\tWeaveSample.Shapes.Area",
        ]);

        Assert.Equal("trace", CecilWeaver.ReadMapMode(old));
        Assert.Single(CecilWeaver.ReadMap(old));
    }

    [Fact]
    public void An_unreadable_header_still_yields_the_safe_answer()
    {
        string odd = Map("odd.map");
        File.WriteAllLines(odd, ["#something else entirely", "1\tA.dll\t0x06000001\tA.B.C"]);

        Assert.Equal("trace", CecilWeaver.ReadMapMode(odd));
    }
    /// <summary>
    /// An app that carries its assemblies inside the APK cannot be woven on the device, but a
    /// build-time weave map means it was woven already - and engine=auto used to fall back to the
    /// runtime provider anyway, which such an app then refuses, ending the session with advice to
    /// "use a build-time weave map" that had just been given.
    /// </summary>
    [Fact]
    public void Auto_picks_the_weaver_when_the_build_already_wove_the_app()
    {
        Assert.Equal(InstrumentingEngine.WeaverTree, ProfilerSession.ChooseEngine(isDebuggable: true, hasAssemblyStore: true, "nap-weave.map"));
        Assert.Equal(InstrumentingEngine.WeaverTree, ProfilerSession.ChooseEngine(isDebuggable: true, hasAssemblyStore: false, null));
        Assert.Equal(InstrumentingEngine.RuntimeProvider, ProfilerSession.ChooseEngine(isDebuggable: true, hasAssemblyStore: true, null));
    }
}
