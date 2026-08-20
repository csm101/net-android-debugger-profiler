using System.Reflection;
using NetAndroidProfiler.Core.Weaving;

namespace NetAndroidProfiler.Tests.Fast;

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
public class WeaverTests
{
    [Fact]
    public void Weave_execute_and_analyze_end_to_end()
    {
        string work = Path.Combine(Path.GetTempPath(), "net-android-profiler-tests", "weave", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        string eventsDir = Path.Combine(work, "events");
        Environment.SetEnvironmentVariable("NAP_PROFILER_OUT", eventsDir);

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
