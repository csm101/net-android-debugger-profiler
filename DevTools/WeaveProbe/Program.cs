// Fast off-device weaver iteration: weave a local assembly with a callspec and
// a resolver over its build-output folder, so write-time Cecil failures on a
// real assembly reproduce in seconds.
//   WeaveProbe <assembly.dll> <callspec> [searchDir ...]
using NetAndroidProfiler.Core.Weaving;

if (args.Length >= 2 && args[0] == "--analyze")
{
    // Re-run the weaver analysis over a session's event files, off the device: this is
    // how a suspicious number in the GUI gets traced back to the raw records.
    var map = args.Length >= 3
        ? NetAndroidProfiler.Core.Weaving.CecilWeaver.ReadMap(args[2])
        : new List<NetAndroidProfiler.Core.Weaving.WovenMethod>();
    var result = new NetAndroidProfiler.Core.Weaving.WeaveAnalyzer().Analyze(args[1], map);
    Console.WriteLine($"enter={result.EnterEvents} leave={result.LeaveEvents} allocs={result.AllocationEvents} broken={result.BrokenPairs} methods={result.Methods.Count}");
    foreach (var t in result.Timings.OrderByDescending(t => t.Calls).Take(12))
        Console.WriteLine($"{t.Calls,10} calls {t.TotalNs,16} total {t.SelfNs,16} self  {result.Method(t.MethodId).FullName}");
    return 0;
}

if (args.Length == 2 && args[0] == "--state-machines")
{
    // Which compiler-generated state machines does a real assembly actually carry?
    var module = Mono.Cecil.ModuleDefinition.ReadModule(args[1]);
    var kinds = new Dictionary<string, List<string>>();
    foreach (var type in module.GetTypes())
        foreach (var method in type.Methods)
            foreach (var attribute in method.CustomAttributes)
                if (attribute.AttributeType.Name.EndsWith("StateMachineAttribute", StringComparison.Ordinal))
                {
                    if (!kinds.TryGetValue(attribute.AttributeType.FullName, out var list))
                        kinds[attribute.AttributeType.FullName] = list = new List<string>();
                    list.Add($"{type.FullName}.{method.Name}");
                }
    foreach (var (name, list) in kinds.OrderByDescending(k => k.Value.Count))
    {
        Console.WriteLine($"{list.Count,6}  {name}");
        foreach (var sample in list.Take(3)) Console.WriteLine($"          {sample}");
    }
    if (kinds.Count == 0) Console.WriteLine("no state machine attributes at all");
    return 0;
}

if (args.Length < 2) { Console.Error.WriteLine("usage: WeaveProbe <assembly.dll> <callspec> [searchDir...]"); return 2; }
string input = args[0], callspec = args[1];
var resolver = new Mono.Cecil.DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(input))!);
for (int i = 2; i < args.Length; i++) resolver.AddSearchDirectory(args[i]);
var w = new CecilWeaver(WeaveFilter.Parse(callspec));
string outp = Path.Combine(Path.GetTempPath(), "weaveprobe-out.dll");
try
{
    var r = w.Weave(input, outp, resolver);
    Console.WriteLine($"OK: woven {r.Methods.Count}, skipped {w.SkippedCount}, wrote {new FileInfo(outp).Length} bytes");
    return 0;
}
catch (Exception e)
{
    Console.WriteLine($"FAIL after weaving {w.Map.Count} (skipped {w.SkippedCount}): {e.GetType().Name}: {e.Message}");
    Console.WriteLine(e.StackTrace);
    return 1;
}
