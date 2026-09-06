// nap-weave: weave assemblies at build time so an app can be profiled with the
// IL-weaving engine even when it ships its assemblies inside the APK
// (EmbedAssembliesIntoApk=true), where rewriting the on-device copies has no effect.
//
//   nap-weave --assembly <path> [--assembly <path>...] --callspec <spec> --map <file>
//             [--reference-dir <dir>...] [--collector-out <dir>] [--first-id <n>] [--quiet]
//             [--property-accessors] [--allocations] [--no-async-bodies]
//
// Each assembly is rewritten in place (a .naporig backup is kept next to it) and the
// method id map is written to --map, which the profiler reads instead of weaving on
// the device. --collector-out copies NetAndroidProfiler.Collector.dll next to the app
// output so the build packages it.

using NetAndroidProfiler.Core.Weaving;

var assemblies = new List<string>();
var referenceDirs = new List<string>();
string? callspec = null, mapPath = null, collectorOut = null;
// How the woven app will record. It is the build that decides, because a build-time weave
// bakes the collector's environment into the app: the session can only follow.
string mode = "tree";
// An app is more than its own assembly - the business logic usually lives in a library -
// so a build weaves in two passes: the app from the intermediate output, its libraries
// from the copies that will be packaged. The second pass adds to the first one's map
// instead of replacing it, and continues its ids.
bool append = false;
int firstId = 1;
bool quiet = false, weaveAccessors = false, trackAllocations = false, asyncBodies = true;

for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    string Next(string name) => ++i < args.Length ? args[i] : throw new ArgumentException($"{name} needs a value");
    switch (a)
    {
        case "--assembly": assemblies.Add(Next(a)); break;
        case "--callspec": callspec = Next(a); break;
        case "--mode": mode = Next(a); break;
        case "--append": append = true; break;
        case "--map": mapPath = Next(a); break;
        case "--reference-dir": referenceDirs.Add(Next(a)); break;
        case "--collector-out": collectorOut = Next(a); break;
        case "--first-id": firstId = int.Parse(Next(a)); break;
        case "--quiet": quiet = true; break;
        case "--property-accessors": weaveAccessors = true; break;
        case "--allocations": trackAllocations = true; break;
        case "--async-bodies": asyncBodies = true; break;
        case "--no-async-bodies": asyncBodies = false; break;
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

/// <summary>
/// Whether an assembly already carries the instrumentation: the woven code calls the
/// collector, so the collector is among its assembly references. This is what tells a
/// build's own output apart from something this tool produced earlier.
/// </summary>
static bool IsWoven(string path)
{
    try
    {
        using var module = Mono.Cecil.ModuleDefinition.ReadModule(path);
        return module.AssemblyReferences.Any(r =>
            string.Equals(r.Name, "NetAndroidProfiler.Collector", StringComparison.OrdinalIgnoreCase));
    }
    catch (Exception)
    {
        return false;           // unreadable is not woven; the weave below will report it
    }
}

try
{
    var filter = WeaveFilter.Parse(callspec);
    // Continuing after another pass: ids must not collide, and the map has to keep what
    // is already in it.
    IReadOnlyList<WovenMethod> existing = append && File.Exists(mapPath!)
        ? CecilWeaver.ReadMap(mapPath!)
        : [];
    if (existing.Count > 0) firstId = existing.Max(m => m.Id) + 1;
    var weaver = new CecilWeaver(filter, firstId, weaveAccessors, trackAllocations, asyncBodies);
    var resolver = new Mono.Cecil.DefaultAssemblyResolver();
    foreach (var dir in referenceDirs)
        if (Directory.Exists(dir)) resolver.AddSearchDirectory(dir);

    foreach (string assembly in assemblies)
    {
        if (!File.Exists(assembly)) { Console.Error.WriteLine($"nap-weave: assembly not found: {assembly}"); return 1; }
        string backup = assembly + ".naporig";
        // Re-weaving an already woven assembly would double every Enter/Leave, so a
        // pristine copy is kept - but the question is whether *this* file is one we wove,
        // not whether a backup exists. Taking the backup as the truth threw away every
        // compilation after the first and shipped a stale app: the build produced new IL,
        // the tool overwrote it with a copy from days earlier, and the app then referenced
        // types that no longer existed in its own libraries.
        if (IsWoven(assembly))
        {
            if (!File.Exists(backup))
            {
                Console.Error.WriteLine(
                    $"nap-weave: {Path.GetFileName(assembly)} is already instrumented and its {Path.GetFileName(backup)} " +
                    "is gone, so the original cannot be recovered. Rebuild the project (a clean build restores it).");
                return 1;
            }
            File.Copy(backup, assembly, overwrite: true);
        }
        else
        {
            // A fresh compilation: this is the new pristine copy.
            File.Copy(assembly, backup, overwrite: true);
        }

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

    if (weaver.Map.Count == 0 && existing.Count > 0)
    {
        // Nothing new here, but the first pass's map must survive this run.
        if (!quiet) Console.WriteLine($"nap-weave: nothing more matched '{callspec}'; the map keeps its {existing.Count} methods");
        return 0;
    }

    if (weaver.Map.Count == 0)
    {
        // Not an error: the same targets file may be imported for several projects.
        // A session that finds no map fails with a clear message of its own.
        Console.WriteLine($"nap-weave: warning: the filter '{callspec}' matched no method in {string.Join(", ", assemblies.Select(Path.GetFileName))}; nothing was woven");
        return 0;
    }
    // The app records what its build baked into it, so the map says which: a session that
    // asked for the other one would otherwise wait for files nobody writes.
    weaver.WriteMap(mapPath, mode, existing);
    if (!quiet)
    {
        Console.WriteLine($"nap-weave: map written to {mapPath} ({weaver.Map.Count} methods, {weaver.SkippedAccessorCount} property accessors skipped)");
        if (weaver.AsyncStubCount > 0)
            Console.WriteLine($"nap-weave: {weaver.AsyncStubCount} async methods woven; {weaver.AsyncBodyCount} of their state machines are instrumented as '<method> (async body)'");
        if (weaver.IteratorBodyCount > 0)
            Console.WriteLine($"nap-weave: {weaver.IteratorBodyCount} iterators are instrumented as '<method> (iterator body)' - one call per item produced");
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

      --append         add to the map that is already there, continuing its ids: how a
                       build weaves its libraries after its own assembly.
      --mode           tree (a call tree kept in the app, the default) or trace (an event
                       per call). Must match what the app's environment asks the collector
                       for; the build's targets set both.
      --callspec       all | N:Namespace | T:Full.Type | M:Full.Type:Method, comma separated,
                       '-' prefix excludes. Keep it narrow: every woven method costs an
                       Enter/Leave pair per call.
      --map            output file mapping method ids to names (the profiler reads it).
      --reference-dir  extra directories for resolving the assemblies' references.
      --collector-out  copy NetAndroidProfiler.Collector.dll into this directory.
      --property-accessors  also weave property getters/setters (skipped by default).
      --allocations    also record allocations made by the woven methods.
      --no-async-bodies  weave only the synchronous stub of async methods. By
                       default the compiler-generated state machine is woven too,
                       reported as '<method> (async body)'; without it an async
                       method only reports its prologue up to the first await.
    """);
