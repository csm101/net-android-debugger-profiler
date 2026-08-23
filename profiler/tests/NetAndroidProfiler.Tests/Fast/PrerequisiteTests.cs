using NetAndroidProfiler.Core.Apps;

namespace NetAndroidProfiler.Tests.Fast;

/// <summary>
/// What the profiler says before it starts. These messages are the product for a user
/// whose app is not ready: they have to name the problem, the build switch that fixes it,
/// and where to read more - and blocking must mean blocking, so a session refuses instead
/// of collecting an empty trace and calling it a result.
/// </summary>
public class PrerequisiteTests
{
    private static AppPrerequisites App(
        bool diagnostics = true, bool debuggable = true, bool aot = false, bool baked = false) =>
        new("com.example.app", ["/data/app/base.apk"], "arm64-v8a", debuggable, diagnostics, aot, baked, []);

    [Fact]
    public void An_app_without_the_diagnostics_component_is_refused_in_every_mode()
    {
        foreach (var mode in new[] { ProfilingMode.Sampling, ProfilingMode.Instrumenting, ProfilingMode.HeapSnapshot })
        {
            var problems = App(diagnostics: false).Check(mode);
            var blocking = problems.Where(p => p.IsBlocking).ToList();
            Assert.NotEmpty(blocking);
            Assert.Contains(blocking, p => p.Message.Contains("libmono-component-diagnostics_tracing.so"));
            // The switch that fixes it, not just the symptom.
            Assert.Contains(blocking, p => p.Message.Contains("-p:EnableDiagnostics=true"));
        }
    }

    [Fact]
    public void A_ready_app_has_nothing_to_say()
    {
        Assert.Empty(App().Check(ProfilingMode.Sampling));
        Assert.Empty(App().Check(ProfilingMode.Instrumenting));
        Assert.Empty(App().Check(ProfilingMode.HeapSnapshot));
    }

    [Fact]
    public void Instrumenting_a_release_app_needs_mono_diagnostics_baked_in()
    {
        var release = App(debuggable: false);
        var problems = release.Check(ProfilingMode.Instrumenting);
        Assert.Contains(problems, p => p.IsBlocking && p.Message.Contains("MONO_DIAGNOSTICS"));

        // A Release build that carries it is fine, and the other modes never needed it:
        // only instrumenting depends on the runtime reading that variable at startup.
        Assert.Empty(App(debuggable: false, baked: true).Check(ProfilingMode.Instrumenting));
        Assert.Empty(release.Check(ProfilingMode.Sampling));
        Assert.Empty(release.Check(ProfilingMode.HeapSnapshot));
    }

    [Fact]
    public void Aot_blocks_instrumenting_and_only_warns_for_sampling()
    {
        var aot = App(aot: true);

        // AOT methods are never instrumented: there is nothing to collect, so refuse.
        var instrumenting = aot.Check(ProfilingMode.Instrumenting);
        Assert.Contains(instrumenting, p => p.IsBlocking && p.Message.Contains("libaot-"));
        Assert.Contains(instrumenting, p => p.Message.Contains("-p:RunAOTCompilation=false"));

        // Sampling still works, with leaf frames landing on the caller: say so and carry on.
        var sampling = aot.Check(ProfilingMode.Sampling);
        Assert.NotEmpty(sampling);
        Assert.All(sampling, p => Assert.False(p.IsBlocking));
        Assert.Contains(sampling, p => p.Message.Contains("leaf frames"));
    }
}
