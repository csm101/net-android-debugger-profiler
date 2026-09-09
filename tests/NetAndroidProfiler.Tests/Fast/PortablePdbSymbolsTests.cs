using NetAndroidProfiler.Core.Analysis;
using NetAndroidProfiler.Core.Symbols;

namespace NetAndroidProfiler.Tests.Fast;

public class PortablePdbSymbolsTests
{
    private static string PdbDir => Path.Combine(AppContext.BaseDirectory, "recorded");

    [Fact]
    public void Loads_testtarget_pdb_and_lists_its_documents()
    {
        using var s = PortablePdbSymbols.LoadDirectory(PdbDir);
        Assert.Contains("TestTarget", s.Modules, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(s.Documents(), d => d.EndsWith("CpuBurner.cs", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The weave map records the file it rewrote ("TestTarget.dll"), a sampling trace the
    /// assembly ("TestTarget"). Both must find the same pdb: when they did not, every
    /// instrumenting session came out with no source locations whatsoever, on an app whose
    /// symbols the session had all along.
    /// </summary>
    [Fact]
    public void A_module_named_with_its_file_extension_finds_the_same_pdb()
    {
        using var s = PortablePdbSymbols.LoadDirectory(PdbDir);
        var method = s.MethodsInDocument("Workloads/CpuBurner.cs").First();

        var byFileName = s.Find(method.Module + ".dll", method.Token);

        Assert.NotNull(byFileName);
        Assert.Equal(s.Find(method.Module, method.Token)!.Document, byFileName!.Document);
    }

    /// <summary>
    /// A suffix short enough to match two files used to be annotated as if it were one: the
    /// figures of every matching file were printed against the text of the first one, so the
    /// numbers landed on lines of a namesake in another project. The caller has to be told.
    /// </summary>
    [Fact]
    public void A_suffix_that_matches_several_source_files_is_reported_as_ambiguous()
    {
        using var s = PortablePdbSymbols.LoadDirectory(PdbDir);

        Assert.Single(s.DocumentsMatching("Workloads/CpuBurner.cs"));
        Assert.True(s.DocumentsMatching(".cs").Count > 1, "the recorded pdbs hold more than one source file");
    }

    [Fact]
    public void Methods_in_document_have_line_ranges()
    {
        using var s = PortablePdbSymbols.LoadDirectory(PdbDir);
        var methods = s.MethodsInDocument("Workloads/CpuBurner.cs");
        Assert.True(methods.Count >= 2, "Busy and Mix expected");
        Assert.All(methods, m => Assert.True(m.StartLine > 0 && m.EndLine >= m.StartLine));
        Assert.All(methods, m => Assert.Equal(0x06, m.Token >> 24));
    }

    [Fact]
    public void Sampled_method_tokens_resolve_to_source_ranges()
    {
        // Tokens come from the trace rundown; the pdb is from a later Debug build of the same source,
        // so the tokens match (method order unchanged) and Busy's range must lie in CpuBurner.cs.
        var r = new SamplingAnalyzer().Analyze(Recorded.SamplingJit20s);
        var busy = r.Methods.Single(m => m.FullName.Contains("CpuBurner.Busy"));
        Assert.NotEqual(0, busy.Token);
        using var s = PortablePdbSymbols.LoadDirectory(PdbDir);
        var range = s.Find(busy.Module, busy.Token);
        Assert.NotNull(range);
        Assert.EndsWith("CpuBurner.cs", range!.Document, StringComparison.OrdinalIgnoreCase);
        Assert.True(range.EndLine > range.StartLine);
    }

    [Fact]
    public void Unknown_module_or_token_returns_null()
    {
        using var s = PortablePdbSymbols.LoadDirectory(PdbDir);
        Assert.Null(s.Find("NoSuchModule", 0x06000001));
        Assert.Null(s.Find("TestTarget", 0x06FFFFFF));
        Assert.Null(s.Find("TestTarget", 0x02000002));
    }
}
