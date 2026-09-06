using Mono.Cecil;
using NetAndroidProfiler.Core.Symbols;

namespace NetAndroidProfiler.Tests.Fast;

/// <summary>
/// Symbolication of the shapes a real app is full of and a hand-written sample is not:
/// methods of generic types, generic methods, and the compiler-generated state machines
/// behind async and iterators. A trace names those by their metadata token, so the test
/// takes the tokens from the assembly itself - which is what the weaver does - and asks
/// the symbol reader where each one lives.
/// </summary>
public class GenericAndStateMachineSymbolsTests
{
    private const string Module = "WeaveSample";

    /// <summary>A copy of the sample's pdb on its own, so the reader is not handed the whole test output.</summary>
    private static string PdbDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nap-symbols-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var extension in new[] { ".dll", ".pdb" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, Module + extension), Path.Combine(dir, Module + extension));
        return dir;
    }

    /// <summary>Metadata tokens of the sample's methods, by the name a trace would carry.</summary>
    private static Dictionary<string, int> Tokens()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Path.Combine(AppContext.BaseDirectory, Module + ".dll"));
        var tokens = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var type in assembly.MainModule.GetTypes())
            foreach (var method in type.Methods)
                if (method.HasBody)
                    tokens[$"{type.FullName}.{method.Name}"] = method.MetadataToken.ToInt32();
        return tokens;
    }

    [Fact]
    public void Methods_of_a_generic_type_and_generic_methods_resolve_to_source()
    {
        var tokens = Tokens();
        string dir = PdbDirectory();
        try
        {
            using var symbols = PortablePdbSymbols.LoadDirectory(dir);

            // A generic type carries `1 in its metadata name; a generic method has one token
            // whatever it is instantiated with, so one source range serves every instantiation.
            foreach (var name in new[] { "WeaveSample.Generic`1.Keep", "WeaveSample.Generic`1.Map" })
            {
                Assert.True(tokens.ContainsKey(name), $"{name} missing from the sample assembly");
                var range = symbols.Find(Module, tokens[name]);
                Assert.True(range is not null, $"{name} did not resolve to a source range");
                Assert.EndsWith("SampleWork.cs", range!.Document, StringComparison.OrdinalIgnoreCase);
                Assert.True(range.EndLine >= range.StartLine && range.StartLine > 0);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// What a sampling trace actually names for an async method or an iterator is the
    /// state machine's MoveNext, not the method the user wrote. It has to resolve, and it
    /// has to resolve into the file the user wrote - otherwise the Source panel and
    /// profile_annotate_source have nothing to show for exactly the methods that wait.
    /// </summary>
    [Fact]
    public void State_machine_move_next_resolves_into_the_authors_file()
    {
        var tokens = Tokens();
        string dir = PdbDirectory();
        try
        {
            using var symbols = PortablePdbSymbols.LoadDirectory(dir);
            var machines = tokens.Keys
                .Where(k => k.Contains("d__", StringComparison.Ordinal) && k.EndsWith(".MoveNext", StringComparison.Ordinal))
                .ToList();
            // One for AddAsync, one for Squares.
            Assert.True(machines.Count >= 2, "the sample must carry an async method and an iterator: " + string.Join(", ", machines));

            foreach (var machine in machines)
            {
                var range = symbols.Find(Module, tokens[machine]);
                Assert.True(range is not null, $"{machine} did not resolve to a source range");
                Assert.EndsWith("SampleWork.cs", range!.Document, StringComparison.OrdinalIgnoreCase);
                Assert.True(range.StartLine > 0);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
