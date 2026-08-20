// nap-weave: weave assemblies at build time so an app can be profiled with the
// IL-weaving engine even when it ships its assemblies inside the APK
// (EmbedAssembliesIntoApk=true), where rewriting the on-device copies has no effect.
//
//   nap-weave --assembly <path> [--assembly <path>...] --callspec <spec> --map <file>
//             [--reference-dir <dir>...] [--collector-out <dir>] [--first-id <n>] [--quiet]
//
// Each assembly is rewritten in place (a .naporig backup is kept next to it) and the
// method id map is written to --map, which the profiler reads instead of weaving on
// the device. --collector-out copies NetAndroidProfiler.Collector.dll next to the app
// output so the build packages it.

using NetAndroidProfiler.Core.Weaving;

var assemblies = new List<string>();
var referenceDirs = new List<string>();
string? callspec = null, mapPath = null, collectorOut = null;
int firstId = 1;
bool quiet = false, weaveAccessors = false, trackAllocations = false;

for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    string Next(string name) => ++i < args.Length ? args[i] : throw new ArgumentException($"{name} needs a value");
    switch (a)
    {
        case "--assembly": assemblies.Add(Next(a)); break;
        case "--callspec": callspec = Next(a); break;
        case "--map": mapPath = Next(a); break;
        case "--reference-dir": referenceDirs.Add(Next(a)); break;
        case "--collector-out": collectorOut = Next(a); break;
        case "--first-id": firstId = int.Parse(Next(a)); break;
        case "--quiet": quiet = true; break;
        case "--property-accessors": weaveAccessors = true; break;
        case "--allocations": trackAllocations = true; break;
        case "-h" or "--help": Usage(); return 0;
        default: Console.Error.WriteLine($"nap-weave: unknown argument '{a}'"); Usage(); return 2;
    }
}

if (assemblies.Count == 0 || callspec is null || mapPath is null)
{
    Console.Error.WriteLine("nap-weave: --assembly, --callspec and --map are required");
    Usage();
    return 2;
}

try
{
    var filter = WeaveFilter.Parse(callspec);
    var weaver = new CecilWeaver(filter, firstId, weaveAccessors, trackAllocations);
    var resolver = new Mono.Cecil.DefaultAssemblyResolver();
    foreach (var dir in referenceDirs)
        if (Directory.Exists(dir)) resolver.AddSearchDirectory(dir);

    foreach (string assembly in assemblies)
    {
        if (!File.Exists(assembly)) { Console.Error.WriteLine($"nap-weave: assembly not found: {assembly}"); return 1; }
        string backup = assembly + ".naporig";
        // Re-weaving an already woven assembly would double every Enter/Leave: always
        // start from the pristine copy when a backup from a previous build exists.
        if (File.Exists(backup)) File.Copy(backup, assembly, overwrite: true);
        else File.Copy(assembly, backup, overwrite: true);

        string temp = assembly + ".napwoven";
        var result = weaver.Weave(backup, temp, resolver);
        if (result.Methods.Count == 0)
        {
            File.Delete(temp);
            if (!quiet) Console.WriteLine($"nap-weave: {Path.GetFileName(assembly)}: no method matched the filter");
            continue;
        }
        File.Copy(temp, assembly, overwrite: true);
        File.Delete(temp);
        if (!quiet) Console.WriteLine($"nap-weave: {Path.GetFileName(assembly)}: {result.Methods.Count} methods woven" +
                                      (weaver.SkippedCount > 0 ? $" ({weaver.SkippedCount} skipped)" : ""));
    }

    if (weaver.Map.Count == 0)
    {
        // Not an error: the same targets file may be imported for several projects.
        // A session that finds no map fails with a clear message of its own.
        Console.WriteLine($"nap-weave: warning: the filter '{callspec}' matched no method in {string.Join(", ", assemblies.Select(Path.GetFileName))}; nothing was woven");
        return 0;
    }
    weaver.WriteMap(mapPath);
    if (!quiet)
    {
        Console.WriteLine($"nap-weave: map written to {mapPath} ({weaver.Map.Count} methods, {weaver.SkippedAccessorCount} property accessors skipped)");
        if (weaver.AsyncStubCount > 0)
            Console.WriteLine($"nap-weave: {weaver.AsyncStubCount} woven methods are async - their timing is the synchronous part up to the first await");
    }

    if (collectorOut is not null)
    {
        string source = Path.Combine(AppContext.BaseDirectory, CecilWeaver.CollectorAssemblyName + ".dll");
        if (!File.Exists(source)) { Console.Error.WriteLine($"nap-weave: collector assembly not found next to the tool ({source})"); return 1; }
        Directory.CreateDirectory(collectorOut);
        string target = Path.Combine(collectorOut, CecilWeaver.CollectorAssemblyName + ".dll");
        File.Copy(source, target, overwrite: true);
        if (!quiet) Console.WriteLine($"nap-weave: collector copied to {target}");
    }
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"nap-weave: {e.GetType().Name}: {e.Message}");
    return 1;
}

static void Usage() => Console.Error.WriteLine("""
    usage: nap-weave --assembly <path> [--assembly <path>...] --callspec <spec> --map <file>
                     [--reference-dir <dir>...] [--collector-out <dir>] [--first-id <n>] [--quiet]

      --callspec       all | N:Namespace | T:Full.Type | M:Full.Type:Method, comma separated,
                       '-' prefix excludes. Keep it narrow: every woven method costs an
                       Enter/Leave pair per call.
      --map            output file mapping method ids to names (the profiler reads it).
      --reference-dir  extra directories for resolving the assemblies' references.
      --collector-out  copy NetAndroidProfiler.Collector.dll into this directory.
      --property-accessors  also weave property getters/setters (skipped by default).
      --allocations    also record allocations made by the woven methods.
    """);
