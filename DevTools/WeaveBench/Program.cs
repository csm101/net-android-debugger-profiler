// What a call costs, measured rather than argued about.
//
//   WeaveBench off|disabled|trace|tree [fibN]
//
// off        the original assembly: no instrumentation at all
// disabled   woven, collector loaded but no output directory (the static check only)
// trace      woven, one record per enter and per leave (the event stream)
// tree       woven, a calling context tree kept in the process
//
// The mode has to be chosen before the collector's static constructor runs, which is why
// this is one process per measurement instead of one process with a loop.
using System.Diagnostics;
using System.Reflection;
using NetAndroidProfiler.Core.Weaving;

string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "off";
int n = args.Length > 1 ? int.Parse(args[1]) : 27;

string work = Path.Combine(Path.GetTempPath(), "nap-bench-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
string original = Path.Combine(AppContext.BaseDirectory, "WeaveSample.dll");
string assemblyPath = original;

if (mode != "off")
{
    assemblyPath = Path.Combine(work, "WeaveSample.dll");
    var weaver = new CecilWeaver(WeaveFilter.Parse("T:WeaveSample.SampleWork"));
    var result = weaver.Weave(original, assemblyPath);
    Console.Error.WriteLine($"woven {result.Methods.Count} methods");
}

if (mode is "trace" or "tree")
{
    string events = Path.Combine(work, "events");
    Directory.CreateDirectory(events);
    Environment.SetEnvironmentVariable("NAP_PROFILER_OUT", events);
    Environment.SetEnvironmentVariable("NAP_PROFILER_MODE", mode == "tree" ? "tree" : "trace");
}

var assembly = Assembly.LoadFile(assemblyPath);
var type = assembly.GetType("WeaveSample.SampleWork")!;
object instance = Activator.CreateInstance(type)!;
var fib = (Func<int, long>)type.GetMethod("Fib")!.CreateDelegate(typeof(Func<int, long>), instance);

fib(5);                                     // let everything load before the clock starts
long calls = Calls(n);
var sw = Stopwatch.StartNew();
long value = fib(n);
sw.Stop();

NetAndroidProfiler.Collector.Profiler.FlushAll();
long bytes = Directory.Exists(Path.Combine(work, "events"))
    ? Directory.GetFiles(Path.Combine(work, "events")).Sum(f => new FileInfo(f).Length)
    : 0;

Console.WriteLine($"{mode,-9} fib({n})={value}  calls={calls:N0}  {sw.Elapsed.TotalMilliseconds,9:N1} ms  " +
                  $"{calls / sw.Elapsed.TotalSeconds / 1_000_000,6:N2} M calls/s  " +
                  $"{sw.Elapsed.TotalMilliseconds * 1_000_000 / calls,8:N0} ns/call  data={bytes / 1024.0,8:N1} KB");

try { Directory.Delete(work, recursive: true); } catch { }

// Fib(n) makes 2*Fib(n+1)-1 calls counting itself.
static long Calls(int n)
{
    long a = 1, b = 1;
    for (int i = 2; i <= n + 1; i++) { long next = a + b; a = b; b = next; }
    return 2 * b - 1;
}
