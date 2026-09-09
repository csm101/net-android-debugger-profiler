using System.Diagnostics;
using Mono.Cecil;
using NetAndroidProfiler.Core.Weaving;

namespace NetAndroidProfiler.Tests.Fast;

/// <summary>
/// nap-weave keeps a .naporig backup so that weaving an already woven assembly does not
/// double every Enter/Leave. The dangerous half of that is deciding *when* the backup is
/// the right input: taking it whenever it exists threw away every compilation after the
/// first one, and the build shipped code from days earlier. Found on the reference application, where the
/// app then crashed at startup referencing a type its own libraries no longer had.
/// </summary>
public class WeaveToolBackupTests : IDisposable
{
    private readonly string _root;

    public WeaveToolBackupTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "net-android-profiler-tests", "weave-tool", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NetAndroidProfiler.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>Runs the published tool the way the build targets do.</summary>
    private static (int exit, string output) Weave(string assembly, string callspec, string map)
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        string dll = Path.Combine(RepoRoot(), "src", "NetAndroidProfiler.Weave", "bin", configuration, "net10.0", "nap-weave.dll");
        Assert.True(File.Exists(dll), $"nap-weave was not built at {dll}");
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
        {
            dll, "--assembly", assembly, "--callspec", callspec, "--map", map,
            "--reference-dir", AppContext.BaseDirectory, "--collector-out", Path.GetDirectoryName(assembly)!,
        })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    private static bool IsWoven(string path)
    {
        using var module = ModuleDefinition.ReadModule(path);
        return module.AssemblyReferences.Any(r => r.Name == "NetAndroidProfiler.Collector");
    }

    private static bool HasType(string path, string typeName)
    {
        using var module = ModuleDefinition.ReadModule(path);
        return module.GetTypes().Any(t => t.Name == typeName);
    }

    /// <summary>A second compilation of the sample: same assembly plus a type that marks it.</summary>
    private string NextBuildOf(string source, string markerType)
    {
        string produced = Path.Combine(_root, "next-build.dll");
        using (var module = ModuleDefinition.ReadModule(source))
        {
            module.Types.Add(new TypeDefinition("WeaveSample", markerType,
                TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object));
            module.Write(produced);
        }
        return produced;
    }

    [Fact]
    public void The_second_build_is_woven_and_not_replaced_by_the_first_ones_backup()
    {
        string assembly = Path.Combine(_root, "WeaveSample.dll");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "WeaveSample.dll"), assembly);
        string map = Path.Combine(_root, "nap-weave.map");

        var first = Weave(assembly, "T:WeaveSample.Shapes", map);
        Assert.Equal(0, first.exit);
        Assert.True(IsWoven(assembly), "the first build should have been woven: " + first.output);
        Assert.True(File.Exists(assembly + ".naporig"));

        // The compiler runs again and produces different IL. The tool must weave *this*,
        // not the copy it kept from the first build.
        string next = NextBuildOf(Path.Combine(AppContext.BaseDirectory, "WeaveSample.dll"), "MarkerOfTheSecondBuild");
        File.Copy(next, assembly, overwrite: true);

        var second = Weave(assembly, "T:WeaveSample.Shapes", map);
        Assert.Equal(0, second.exit);
        Assert.True(IsWoven(assembly), "the second build should have been woven: " + second.output);
        Assert.True(HasType(assembly, "MarkerOfTheSecondBuild"),
            "the tool wove the backup of the first build instead of the compilation that had just been produced");
        // The backup follows the new compilation, so a third build starts from the right place.
        Assert.True(HasType(assembly + ".naporig", "MarkerOfTheSecondBuild"));
        Assert.False(IsWoven(assembly + ".naporig"), "the backup must stay pristine");
    }

    [Fact]
    public void Weaving_the_same_build_twice_does_not_instrument_it_twice()
    {
        string assembly = Path.Combine(_root, "WeaveSample.dll");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "WeaveSample.dll"), assembly);
        string map = Path.Combine(_root, "nap-weave.map");

        Assert.Equal(0, Weave(assembly, "T:WeaveSample.Shapes", map).exit);
        long afterFirst = new FileInfo(assembly).Length;
        Assert.Equal(0, Weave(assembly, "T:WeaveSample.Shapes", map).exit);

        // Starting from the backup each time is what keeps this from growing.
        Assert.Equal(afterFirst, new FileInfo(assembly).Length);
        Assert.True(IsWoven(assembly));
    }

    [Fact]
    public void An_already_woven_assembly_without_its_backup_is_refused_rather_than_woven_again()
    {
        string assembly = Path.Combine(_root, "WeaveSample.dll");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "WeaveSample.dll"), assembly);
        string map = Path.Combine(_root, "nap-weave.map");
        Assert.Equal(0, Weave(assembly, "T:WeaveSample.Shapes", map).exit);
        File.Delete(assembly + ".naporig");

        var again = Weave(assembly, "T:WeaveSample.Shapes", map);

        Assert.NotEqual(0, again.exit);
        Assert.Contains("already instrumented", again.output);
    }
    /// <summary>
    /// Clear Results deleted the pulled per-call files and left the pulled call trees, which the
    /// next Get Results imported again: on a real app the cleared session came back with every
    /// figure it had before the clear, plus the new ones.
    /// </summary>
    [Fact]
    public void Clearing_deletes_the_pulled_trees_as_well_as_the_pulled_events()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nap-clear-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (string name in new[] { "p1-t2-g0.napw", "p1-t2-g0.napt", "keep.txt" })
                File.WriteAllText(Path.Combine(dir, name), "x");

            WeaveDeployer.DeletePulledEvents(dir);

            Assert.Equal(new[] { "keep.txt" }, Directory.GetFiles(dir).Select(Path.GetFileName).ToArray());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
