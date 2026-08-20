// Fast off-device weaver iteration: weave a local assembly with a callspec and
// a resolver over its build-output folder, so write-time Cecil failures on a
// real assembly reproduce in seconds.
//   WeaveProbe <assembly.dll> <callspec> [searchDir ...]
using NetAndroidProfiler.Core.Weaving;

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
