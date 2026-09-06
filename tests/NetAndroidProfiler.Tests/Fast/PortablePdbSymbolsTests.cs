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
