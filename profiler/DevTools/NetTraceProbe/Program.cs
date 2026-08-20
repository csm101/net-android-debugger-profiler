// DevTools probe: inspect a .nettrace with TraceEvent.
//
//   NetTraceProbe providers <file.nettrace>
//       provider / event name / count table (raw EventPipe stream).
//   NetTraceProbe events <file.nettrace> <providerName> [max] [eventName]
//       dump payload field names/values of the first <max> events of a provider
//       (raw hex when TraceEvent has no schema for the event).
//   NetTraceProbe topn <file.nettrace> [N]
//       sampling hotspots from Microsoft-DotNETCore-SampleProfiler stacks:
//       exclusive and inclusive sample counts per method (via TraceLog).
//   NetTraceProbe stacks <file.nettrace> <providerName> [max]
//       resolved managed call stack of the first <max> events of a provider.
//   NetTraceProbe monoprof <file.nettrace> [N]
//       decode Microsoft-DotNETRuntimeMonoProfiler events by hand (TraceEvent
//       has no parser for them): enter/leave per method (MethodID resolved
//       through rundown MethodLoadVerbose), allocations per VTableID (resolved
//       through VTableLoaded + ClassLoaded when those keywords were enabled).

using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: NetTraceProbe providers|events|topn|stacks|monoprof <file.nettrace> [...]");
    return 2;
}

string cmd = args[0];
string file = args[1];
if (!File.Exists(file))
{
    Console.Error.WriteLine($"file not found: {file}");
    return 2;
}

switch (cmd)
{
    case "providers": return Providers(file);
    case "events": return Events(file, args[2], args.Length > 3 ? int.Parse(args[3]) : 20, args.Length > 4 ? args[4] : null);
    case "topn": return TopN(file, args.Length > 2 ? int.Parse(args[2]) : 30);
    case "stacks": return Stacks(file, args[2], args.Length > 3 ? int.Parse(args[3]) : 5);
    case "monoprof": return MonoProf(file, args.Length > 2 ? int.Parse(args[2]) : 25);
    default:
        Console.Error.WriteLine($"unknown command {cmd}");
        return 2;
}

static int Providers(string file)
{
    var counts = new Dictionary<(string, string), long>();
    using var src = new EventPipeEventSource(file);
    src.Dynamic.All += _ => { }; // activates the dynamic parser so provider/event names resolve
    // AllEvents fires exactly once per event (after the parsers had their turn).
    src.AllEvents += ev =>
    {
        var key = (ev.ProviderName, ev.EventName);
        counts[key] = counts.GetValueOrDefault(key) + 1;
    };
    src.Process();
    Console.WriteLine($"Pointer={src.PointerSize} OS={src.OSVersion} start={src.SessionStartTime:O} dur={src.SessionDuration}");
    foreach (var kv in counts.OrderBy(k => k.Key.Item1).ThenByDescending(k => k.Value))
        Console.WriteLine($"{kv.Value,10}  {kv.Key.Item1}  /  {kv.Key.Item2}");
    return 0;
}

static int Events(string file, string provider, int max, string? eventName)
{
    int n = 0;
    using var src = new EventPipeEventSource(file);
    src.Dynamic.All += _ => { }; // activates the dynamic parser so provider/event names resolve
    src.AllEvents += ev =>
    {
        if (ev.ProviderName != provider) return;
        if (eventName is not null && ev.EventName != eventName) return;
        if (n++ >= max) return;
        Console.WriteLine($"--- #{n} {ev.EventName} id={(int)ev.ID} opcode={ev.Opcode} tid={ev.ThreadID} t={ev.TimeStampRelativeMSec:F3}ms ver={ev.Version} payloadLen={ev.EventDataLength} keywords=0x{(ulong)ev.Keywords:X}");
        for (int i = 0; i < ev.PayloadNames.Length; i++)
        {
            object? v;
            try { v = ev.PayloadValue(i); } catch (Exception e) { v = $"<err {e.GetType().Name}>"; }
            if (v is byte[] bytes) v = $"byte[{bytes.Length}] {Convert.ToHexString(bytes.AsSpan(0, Math.Min(64, bytes.Length)))}";
            Console.WriteLine($"    {ev.PayloadNames[i]} = {v}");
        }
        if (ev.PayloadNames.Length == 0)
        {
            var raw = new byte[Math.Min(96, ev.EventDataLength)];
            Marshal.Copy(ev.DataStart, raw, 0, raw.Length);
            Console.WriteLine($"    <no schema> raw={Convert.ToHexString(raw)}");
        }
    };
    src.Process();
    Console.WriteLine($"matched events: {n}");
    return 0;
}

static int TopN(string file, int n)
{
    string etlx = TraceLog.CreateFromEventPipeDataFile(file);
    using var log = new TraceLog(etlx);
    var exclusive = new Dictionary<string, long>();
    var inclusive = new Dictionary<string, long>();
    long samples = 0, withStack = 0, unresolvedLeaf = 0;
    var seenInStack = new HashSet<string>();
    foreach (var ev in log.Events)
    {
        if (ev.ProviderName != "Microsoft-DotNETCore-SampleProfiler") continue;
        samples++;
        var cs = ev.CallStackIndex();
        if (cs == CallStackIndex.Invalid) continue;
        withStack++;
        seenInStack.Clear();
        bool leaf = true;
        while (cs != CallStackIndex.Invalid)
        {
            var ca = log.CallStacks.CodeAddressIndex(cs);
            string name = FrameName(log, ca);
            if (leaf)
            {
                if (name.StartsWith("?", StringComparison.Ordinal)) unresolvedLeaf++;
                exclusive[name] = exclusive.GetValueOrDefault(name) + 1;
                leaf = false;
            }
            if (seenInStack.Add(name))
                inclusive[name] = inclusive.GetValueOrDefault(name) + 1;
            cs = log.CallStacks.Caller(cs);
        }
    }
    Console.WriteLine($"samples={samples} withStack={withStack} unresolvedLeaf={unresolvedLeaf} threads={log.Threads.Count}");
    Console.WriteLine($"\nTop {n} exclusive:");
    foreach (var kv in exclusive.OrderByDescending(k => k.Value).Take(n))
        Console.WriteLine($"{kv.Value,8} {100.0 * kv.Value / Math.Max(1, withStack),6:F1}%  {kv.Key}");
    Console.WriteLine($"\nTop {n} inclusive:");
    foreach (var kv in inclusive.OrderByDescending(k => k.Value).Take(n))
        Console.WriteLine($"{kv.Value,8} {100.0 * kv.Value / Math.Max(1, withStack),6:F1}%  {kv.Key}");
    return 0;
}

static int Stacks(string file, string provider, int max)
{
    string etlx = TraceLog.CreateFromEventPipeDataFile(file);
    using var log = new TraceLog(etlx);
    int n = 0, total = 0, withStack = 0;
    foreach (var ev in log.Events)
    {
        if (ev.ProviderName != provider) continue;
        total++;
        var cs = ev.CallStackIndex();
        if (cs == CallStackIndex.Invalid) continue;
        withStack++;
        if (n++ >= max) continue;
        Console.WriteLine($"--- {ev.EventName} tid={ev.ThreadID} t={ev.TimeStampRelativeMSec:F3}ms");
        while (cs != CallStackIndex.Invalid)
        {
            Console.WriteLine("    " + FrameName(log, log.CallStacks.CodeAddressIndex(cs)));
            cs = log.CallStacks.Caller(cs);
        }
    }
    Console.WriteLine($"events={total} withStack={withStack}");
    return 0;
}

static int MonoProf(string file, int n)
{
    const string Provider = "Microsoft-DotNETRuntimeMonoProfiler";
    // Event IDs from ClrEtwAll.man, provider Microsoft-DotNETRuntimeMonoProfiler.
    var idNames = new Dictionary<int, string>
    {
        [8] = "JitBegin", [9] = "JitFailed", [10] = "JitDone", [13] = "JitCodeBuffer",
        [16] = "ClassLoaded", [17] = "VTableLoading", [18] = "VTableFailed", [19] = "VTableLoaded",
        [29] = "MethodEnter", [30] = "MethodLeave", [31] = "MethodTailCall", [32] = "MethodExceptionLeave",
        [33] = "MethodFree", [34] = "MethodBeginInvoke", [35] = "MethodEndInvoke",
        [36] = "ExceptionThrow", [37] = "ExceptionClause", [38] = "GCEvent", [39] = "GCAllocation",
        [57] = "ThreadStarted", [58] = "ThreadStopping", [59] = "ThreadStopped", [60] = "ThreadExited",
    };
    var counts = new Dictionary<int, long>();
    var methodNames = new Dictionary<ulong, string>();      // MethodID -> name (rundown + JitDone)
    var jitDoneIds = new HashSet<ulong>();
    var enterByMethod = new Dictionary<ulong, long>();
    var leaveByMethod = new Dictionary<ulong, long>();
    var depthByThread = new Dictionary<int, int>();
    int maxDepth = 0;
    long allocCount = 0, allocBytes = 0;
    var allocByVTable = new Dictionary<ulong, (long count, long bytes)>();
    var vtableToClass = new Dictionary<ulong, ulong>();
    var classNames = new Dictionary<ulong, string>();
    double firstEnter = -1, lastLeave = -1;

    using var src = new EventPipeEventSource(file);
    src.Dynamic.All += _ => { }; // activates the dynamic parser so provider/event names resolve
    src.Clr.MethodLoadVerbose += ev => methodNames[(ulong)ev.MethodID] = $"{ev.MethodNamespace}.{ev.MethodName}";
    var rundown = new ClrRundownTraceEventParser(src);
    rundown.MethodDCStopVerbose += ev => methodNames[(ulong)ev.MethodID] = $"{ev.MethodNamespace}.{ev.MethodName}";
    rundown.MethodDCStartVerbose += ev => methodNames[(ulong)ev.MethodID] = $"{ev.MethodNamespace}.{ev.MethodName}";
    src.AllEvents += ev =>
    {
        if (ev.ProviderName != Provider) return;
        int id = (int)ev.ID;
        counts[id] = counts.GetValueOrDefault(id) + 1;
        switch (id)
        {
            case 29:
            {
                ulong m = ReadU64(ev, 0);
                enterByMethod[m] = enterByMethod.GetValueOrDefault(m) + 1;
                int d = depthByThread.GetValueOrDefault(ev.ThreadID) + 1;
                depthByThread[ev.ThreadID] = d;
                if (d > maxDepth) maxDepth = d;
                if (firstEnter < 0) firstEnter = ev.TimeStampRelativeMSec;
                break;
            }
            case 30:
            case 31:
            case 32:
            {
                ulong m = ReadU64(ev, 0);
                leaveByMethod[m] = leaveByMethod.GetValueOrDefault(m) + 1;
                depthByThread[ev.ThreadID] = depthByThread.GetValueOrDefault(ev.ThreadID) - 1;
                lastLeave = ev.TimeStampRelativeMSec;
                break;
            }
            case 39:
            {
                ulong vt = ReadU64(ev, 0);
                ulong size = ReadU64(ev, 16);
                allocCount++;
                allocBytes += (long)size;
                var cur = allocByVTable.GetValueOrDefault(vt);
                allocByVTable[vt] = (cur.count + 1, cur.bytes + (long)size);
                break;
            }
            case 19:
                vtableToClass[ReadU64(ev, 0)] = ReadU64(ev, 8);
                break;
            case 16:
            {
                ulong cls = ReadU64(ev, 0);
                classNames[cls] = ReadUnicodeZ(ev, 16);
                break;
            }
            case 10:
                jitDoneIds.Add(ReadU64(ev, 0));
                break;
        }
    };
    src.Process();

    Console.WriteLine($"duration={src.SessionDuration} enter-window={firstEnter:F1}..{lastLeave:F1} ms maxDepth={maxDepth}");
    Console.WriteLine("\nEvent counts:");
    foreach (var kv in counts.OrderBy(k => k.Key))
        Console.WriteLine($"{kv.Value,10}  {kv.Key,3} {idNames.GetValueOrDefault(kv.Key, "?")}");

    Console.WriteLine($"\nEnter/leave per method (resolved via rundown; {methodNames.Count} method names known, {jitDoneIds.Count} JitDone ids):");
    int unresolved = 0;
    foreach (var kv in enterByMethod.OrderByDescending(k => k.Value).Take(n))
    {
        string name = methodNames.TryGetValue(kv.Key, out var s) ? s : $"<unresolved 0x{kv.Key:X}>";
        if (name.StartsWith('<')) unresolved++;
        Console.WriteLine($"{kv.Value,10} enter {leaveByMethod.GetValueOrDefault(kv.Key),10} leave  {name}");
    }
    Console.WriteLine($"distinct instrumented methods={enterByMethod.Count} (unresolved in top list: {unresolved})");

    Console.WriteLine($"\nAllocations: {allocCount} objects, {allocBytes} bytes, {allocByVTable.Count} distinct vtables (vtable->class map {vtableToClass.Count}, class names {classNames.Count}):");
    foreach (var kv in allocByVTable.OrderByDescending(k => k.Value.bytes).Take(n))
    {
        string name = vtableToClass.TryGetValue(kv.Key, out var cls) && classNames.TryGetValue(cls, out var cn) ? cn : $"<vtable 0x{kv.Key:X}>";
        Console.WriteLine($"{kv.Value.count,10} objs {kv.Value.bytes,12} bytes  {name}");
    }
    return 0;

    static ulong ReadU64(TraceEvent ev, int offset) => (ulong)Marshal.ReadInt64(ev.DataStart, offset);
    static string ReadUnicodeZ(TraceEvent ev, int offset)
    {
        var chars = new List<char>();
        for (int o = offset; o + 1 < ev.EventDataLength; o += 2)
        {
            char c = (char)Marshal.ReadInt16(ev.DataStart, o);
            if (c == '\0') break;
            chars.Add(c);
        }
        return new string(chars.ToArray());
    }
}

static string FrameName(TraceLog log, CodeAddressIndex ca)
{
    var mi = log.CodeAddresses.MethodIndex(ca);
    if (mi != MethodIndex.Invalid)
        return log.CodeAddresses.Methods.FullMethodName(mi);
    var mod = log.CodeAddresses.ModuleFile(ca);
    return $"?{(mod?.Name ?? "unknown")}!0x{log.CodeAddresses.Address(ca):X}";
}
