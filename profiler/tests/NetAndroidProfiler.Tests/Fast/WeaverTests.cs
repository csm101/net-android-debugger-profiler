using System.Reflection;
using NetAndroidProfiler.Core.Weaving;

namespace NetAndroidProfiler.Tests.Fast;

/// <summary>
/// The collector reads NAP_PROFILER_OUT once per process, in its static constructor,
/// so every test that executes woven code must agree on one output directory and run
/// in the same collection. Events from different tests land in the same files; each
/// test asserts only on the types and methods it produced.
/// </summary>
public sealed class WeaverCollectorFixture
{
    public WeaverCollectorFixture()
    {
        EventsDir = Path.Combine(Path.GetTempPath(), "net-android-profiler-tests", "weave", Guid.NewGuid().ToString("N"), "events");
        Directory.CreateDirectory(EventsDir);
        Environment.SetEnvironmentVariable("NAP_PROFILER_OUT", EventsDir);
    }

    public string EventsDir { get; }

    public string WorkDir(string name) =>
        Path.Combine(Path.GetDirectoryName(EventsDir)!, name);
}

[CollectionDefinition("weaver-collector")]
public sealed class WeaverCollectorCollection : ICollectionFixture<WeaverCollectorFixture> { }

public class WeaveFilterTests
{
    [Fact]
    public void Namespace_type_method_and_exclusions()
    {
        var f = WeaveFilter.Parse("N:WeaveSample,-T:WeaveSample.Untouched,-M:WeaveSample.SampleWork:Boom");
        Assert.True(f.Matches("WeaveSample", "WeaveSample.SampleWork", "Fib"));
        Assert.True(f.Matches("WeaveSample.Sub", "WeaveSample.Sub.X", "Y"));
        Assert.False(f.Matches("WeaveSample", "WeaveSample.Untouched", "NotWoven"));
        Assert.False(f.Matches("WeaveSample", "WeaveSample.SampleWork", "Boom"));
        Assert.False(f.Matches("Other", "Other.T", "M"));
        Assert.True(WeaveFilter.Parse("all,-N:System").Matches("Any", "Any.T", "M"));
        Assert.Throws<FormatException>(() => WeaveFilter.Parse("X:zzz"));
        Assert.Throws<FormatException>(() => WeaveFilter.Parse(""));
    }
}

/// <summary>
/// Weaves the WeaveSample assembly, loads the woven copy in-process (the
/// Collector is already referenced by this test assembly), executes known
/// call shapes and asserts on the recorded events. Single test method: the
/// Collector's static initializer reads NAP_PROFILER_OUT exactly once per
/// process, so the environment variable must be set before anything touches
/// it and every scenario shares one event directory.
/// </summary>
public class WeaverShapeTests
{
    private static string Input => Path.Combine(AppContext.BaseDirectory, "WeaveSample.dll");
    private static string Out(string name) => Path.Combine(Path.GetTempPath(), "net-android-profiler-tests", "weave", Guid.NewGuid().ToString("N"), name);

    [Fact]
    public void Property_accessors_are_skipped_by_default_and_can_be_included()
    {
        var byDefault = new CecilWeaver(WeaveFilter.Parse("T:WeaveSample.Shapes"));
        byDefault.Weave(Input, Out("WeaveSample.dll"));
        Assert.DoesNotContain(byDefault.Map, m => m.FullName.Contains("get_") || m.FullName.Contains("set_"));
        Assert.True(byDefault.SkippedAccessorCount >= 3, $"expected the Counter/Doubled accessors to be skipped, got {byDefault.SkippedAccessorCount}");

        var withAccessors = new CecilWeaver(WeaveFilter.Parse("T:WeaveSample.Shapes"), 1, weavePropertyAccessors: true);
        withAccessors.Weave(Input, Out("WeaveSample.dll"));
        Assert.Contains(withAccessors.Map, m => m.FullName.Contains("get_Counter"));
        Assert.Equal(0, withAccessors.SkippedAccessorCount);
        Assert.True(withAccessors.Map.Count > byDefault.Map.Count);
    }

    [Fact]
    public void Async_methods_are_woven_as_stub_and_state_machine()
    {
        var weaver = new CecilWeaver(WeaveFilter.Parse("T:WeaveSample.Shapes"));
        weaver.Weave(Input, Out("WeaveSample.dll"));
        // The stub (synchronous part up to the first await) ...
        Assert.Contains(weaver.Map, m => m.FullName.EndsWith("Shapes.AddAsync"));
        Assert.Equal(1, weaver.AsyncStubCount);
        // ... and the state machine body (what the method actually executes).
        Assert.Contains(weaver.Map, m => m.FullName == "WeaveSample.Shapes.AddAsync (async body)");
        Assert.Equal(1, weaver.AsyncBodyCount);

        var withoutBodies = new CecilWeaver(WeaveFilter.Parse("T:WeaveSample.Shapes"), 1, weaveAsyncBodies: false);
        withoutBodies.Weave(Input, Out("WeaveSample.dll"));
        Assert.DoesNotContain(withoutBodies.Map, m => m.FullName.Contains("(async body)"));
    }
}

/// <summary>
/// Allocation tracking: weave a method that allocates a known number of objects,
/// run it, and check what the collector recorded. Separate process-wide state from
/// WeaverTests is not needed - both share the collector's single output directory,
/// so this test only asserts on its own types.
/// </summary>
[Collection("weaver-collector")]
public class WeaverAllocationTests(WeaverCollectorFixture fixture)
{
    [Fact]
    public void Async_state_machine_records_every_resumption()
    {
        string work = fixture.WorkDir("async");
        Directory.CreateDirectory(work);
        string woven = Path.Combine(work, "WeaveSample.dll");
        var weaver = new CecilWeaver(WeaveFilter.Parse("T:WeaveSample.Shapes"), 900);
        weaver.Weave(Path.Combine(AppContext.BaseDirectory, "WeaveSample.dll"), woven);

        var asm = System.Reflection.Assembly.LoadFile(woven);
        var type = asm.GetType("WeaveSample.Shapes")!;
        object instance = Activator.CreateInstance(type)!;
        var task = (Task<int>)type.GetMethod("AddAsync")!.Invoke(instance, [2, 3])!;
        Assert.Equal(5, task.GetAwaiter().GetResult());
        NetAndroidProfiler.Collector.Profiler.FlushAll();

        var r = new WeaveAnalyzer().Analyze(fixture.EventsDir, weaver.Map);
        var body = r.Timings.Single(t => r.Method(t.MethodId).FullName == "WeaveSample.Shapes.AddAsync (async body)");
        // Task.Yield suspends once, so the state machine runs twice.
        Assert.Equal(2, body.Calls);
        Assert.True(body.TotalNs > 0);
    }

    [Fact]
    public void Woven_methods_report_their_allocations_by_type_and_site()
    {
        string work = fixture.WorkDir("alloc");
        Directory.CreateDirectory(work);
        string eventsDir = fixture.EventsDir;

        string input = Path.Combine(AppContext.BaseDirectory, "WeaveSample.dll");
        string woven = Path.Combine(work, "WeaveSample.dll");
        var weaver = new CecilWeaver(WeaveFilter.Parse("T:WeaveSample.Allocator"), 500, weavePropertyAccessors: false, trackAllocations: true);
        weaver.Weave(input, woven);
        Assert.True(weaver.AllocationSiteCount >= 3, $"expected the List, Thing and byte[] sites, got {weaver.AllocationSiteCount}");

        var asm = System.Reflection.Assembly.LoadFile(woven);
        var type = asm.GetType("WeaveSample.Allocator")!;
        object instance = Activator.CreateInstance(type)!;
        int result = (int)type.GetMethod("MakeThings")!.Invoke(instance, [7])!;
        Assert.Equal(7 + 64, result);
        NetAndroidProfiler.Collector.Profiler.FlushAll();

        var r = new WeaveAnalyzer().Analyze(eventsDir, weaver.Map);
        Assert.True(r.AllocationEvents >= 9, $"allocation events = {r.AllocationEvents}");

        var things = r.AllocsByType.Single(a => r.Types[a.TypeId].Name == "WeaveSample.Thing");
        Assert.Equal(7, things.Count);

        var bytes = r.AllocsByType.SingleOrDefault(a => r.Types[a.TypeId].Name!.StartsWith("System.Byte["));
        Assert.NotNull(bytes);

        // Every allocation happened inside the woven method, so it must be attributed to it.
        var site = r.AllocsBySite.First(a => a.TypeId == things.TypeId);
        Assert.Equal("WeaveSample.Allocator.MakeThings", r.Method(site.MethodId).FullName);
    }
}

[Collection("weaver-collector")]
public class WeaverTests(WeaverCollectorFixture fixture)
{
    [Fact]
    public void Weave_execute_and_analyze_end_to_end()
    {
        string work = fixture.WorkDir("shapes");
        Directory.CreateDirectory(work);
        string eventsDir = fixture.EventsDir;

        // --- weave
        string input = Path.Combine(AppContext.BaseDirectory, "WeaveSample.dll");
        string woven = Path.Combine(work, "WeaveSample.dll");
        var weaver = new CecilWeaver(WeaveFilter.Parse("N:WeaveSample,-T:WeaveSample.Untouched"));
        var result = weaver.Weave(input, woven);
        Assert.Contains(result.Methods, m => m.FullName == "WeaveSample.SampleWork.Fib");
        Assert.Contains(result.Methods, m => m.FullName == "WeaveSample.SampleWork.Boom");
        Assert.DoesNotContain(result.Methods, m => m.FullName.Contains("Untouched"));
        string mapPath = Path.Combine(work, "weave.map");
        weaver.WriteMap(mapPath);
        Assert.Equal(result.Methods.Count, CecilWeaver.ReadMap(mapPath).Count);

        // --- execute the woven assembly in-process
        var asm = Assembly.LoadFile(woven);
        var type = asm.GetType("WeaveSample.SampleWork")!;
        object obj = Activator.CreateInstance(type)!;
        long fib = (long)type.GetMethod("Fib")!.Invoke(obj, [7])!;       // 1 + 2*Fib recursion: 41 calls for n=7
        Assert.Equal(13, fib);
        string composed = (string)type.GetMethod("Compose")!.Invoke(obj, [3])!;
        Assert.Equal("0,2,4", composed);
        int caught = (int)type.GetMethod("CatchAndReturn")!.Invoke(obj, [])!;
        Assert.Equal(42, caught);
        var boom = Assert.Throws<TargetInvocationException>(() => type.GetMethod("Boom")!.Invoke(obj, []));
        Assert.IsType<InvalidOperationException>(boom.InnerException);
        NetAndroidProfiler.Collector.Profiler.FlushAll();

        // --- analyze
        var r = new WeaveAnalyzer().Analyze(eventsDir, weaver.Map);
        Assert.Equal(r.EnterEvents, r.LeaveEvents);

        var fibT = r.Timings.Single(t => r.Method(t.MethodId).FullName == "WeaveSample.SampleWork.Fib");
        Assert.Equal(41, fibT.Calls);                      // Fib(7) => 41 invocations
        Assert.True(fibT.TotalNs > 0 && fibT.SelfNs > 0 && fibT.SelfNs <= fibT.TotalNs);

        var helper = r.Timings.Single(t => r.Method(t.MethodId).FullName == "WeaveSample.SampleWork.Helper");
        Assert.Equal(3, helper.Calls);

        // Boom: called twice (once caught inside CatchAndReturn, once escaping); the
        // finally-based Leave fires both times, so calls balance even on exceptions.
        var boomT = r.Timings.Single(t => r.Method(t.MethodId).FullName == "WeaveSample.SampleWork.Boom");
        Assert.Equal(2, boomT.Calls);

        var catchT = r.Timings.Single(t => r.Method(t.MethodId).FullName == "WeaveSample.SampleWork.CatchAndReturn");
        Assert.Equal(1, catchT.Calls);

        // Tree: Helper nests under Compose; Fib nests under itself.
        var compose = r.Tree.Single(n => n.MethodId >= 0 && r.Method(n.MethodId).Name == "Compose");
        var helperNode = r.Tree.Single(n => n.ParentId == compose.Id && r.Method(n.MethodId).Name == "Helper");
        Assert.Equal(3, helperNode.Calls);
        var fibRoot = r.Tree.Single(n => n.MethodId >= 0 && r.Method(n.MethodId).Name == "Fib" && r.Tree.First(p => p.Id == n.ParentId!.Value).MethodId == -1);
        var fibChild = r.Tree.Single(n => n.ParentId == fibRoot.Id && r.Method(n.MethodId).Name == "Fib");
        Assert.Equal(2, fibChild.Calls); // Fib(6) and Fib(5) directly under the root call

        // Untouched type must behave normally and produce no events.
        var untouched = asm.GetType("WeaveSample.Untouched")!;
        Assert.Equal(7, (int)untouched.GetMethod("NotWoven")!.Invoke(Activator.CreateInstance(untouched), [])!);
        Assert.DoesNotContain(r.Timings, t => r.Method(t.MethodId).FullName.Contains("Untouched"));
    }
}
