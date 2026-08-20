using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace NetAndroidProfiler.Core.Analysis;

/// <summary>
/// Decodes Microsoft-DotNETRuntimeMonoProfiler events (TraceEvent has no
/// parser for this provider) into method timings, an instrumented call tree
/// and allocation tables. Payload layouts come from ClrEtwAll.man.
/// </summary>
public sealed class MonoProfilerAnalyzer
{
    /// <summary>Provider name as emitted by the Mono runtime.</summary>
    public const string ProviderName = "Microsoft-DotNETRuntimeMonoProfiler";

    /// <summary>Provider keywords (ClrEtwAll.man, Microsoft-DotNETRuntimeMonoProfiler).</summary>
    public static class Keywords
    {
        public const ulong Gc = 0x1;
        public const ulong Jit = 0x10;
        public const ulong Exception = 0x8000;
        public const ulong GcHeapDump = 0x100000;
        public const ulong GcAllocation = 0x200000;
        public const ulong GcHeapDumpVTableClassReference = 0x8000000;
        public const ulong MethodTracing = 0x20000000;
        public const ulong TypeLoading = 0x8000000000;
        public const ulong MethodInstrumentation = 0x40000000000;

        /// <summary>Enter/leave with type names and JIT events - what an instrumenting session enables.</summary>
        public const ulong Instrumenting = MethodInstrumentation | MethodTracing | TypeLoading | Jit | Gc;
        /// <summary>Instrumenting plus exact allocation events.</summary>
        public const ulong InstrumentingWithAllocations = Instrumenting | GcAllocation;
    }

    /// <summary>Event ids used here.</summary>
    private static class Events
    {
        public const int JitDone = 10;
        public const int ClassLoaded = 16;
        public const int VTableLoaded = 19;
        public const int MethodEnter = 29;
        public const int MethodLeave = 30;
        public const int MethodTailCall = 31;
        public const int MethodExceptionLeave = 32;
        public const int GcEvent = 38;
        public const int GcAllocation = 39;
    }

    private readonly WaitFrameClassifier _waitFrames;

    public MonoProfilerAnalyzer(WaitFrameClassifier? waitFrames = null)
    {
        _waitFrames = waitFrames ?? WaitFrameClassifier.Default;
    }

    public InstrumentingResult Analyze(string netTracePath, CancellationToken ct = default)
    {
        if (!File.Exists(netTracePath))
            throw new FileNotFoundException("Trace file not found", netTracePath);

        var methods = new MethodTable(_waitFrames);
        var runtimeNames = new Dictionary<ulong, (string ns, string name, string sig, string module)>();
        var timings = new Dictionary<int, TimingAccumulator>();
        var threads = new Dictionary<int, ThreadState>();
        var tree = new TreeBuilder();
        var vtableToClass = new Dictionary<ulong, ulong>();
        var classNames = new Dictionary<ulong, string>();
        var allocByVTable = new Dictionary<ulong, (long count, long bytes)>();
        var allocBySite = new Dictionary<(ulong vt, ulong method), (long count, long bytes)>();
        long enters = 0, leaves = 0, allocs = 0, gcs = 0;

        using var src = new EventPipeEventSource(netTracePath);
        src.Dynamic.All += _ => { }; // activates name resolution for unknown providers
        var rundown = new ClrRundownTraceEventParser(src);
        rundown.MethodDCStopVerbose += ev => runtimeNames[(ulong)ev.MethodID] = (ev.MethodNamespace, ev.MethodName, ev.MethodSignature, ModuleName(ev.ModuleID));
        rundown.MethodDCStartVerbose += ev => runtimeNames[(ulong)ev.MethodID] = (ev.MethodNamespace, ev.MethodName, ev.MethodSignature, ModuleName(ev.ModuleID));
        src.Clr.MethodLoadVerbose += ev => runtimeNames[(ulong)ev.MethodID] = (ev.MethodNamespace, ev.MethodName, ev.MethodSignature, ModuleName(ev.ModuleID));

        src.AllEvents += ev =>
        {
            if (ev.ProviderName != ProviderName) return;
            ct.ThrowIfCancellationRequested();
            int id = (int)ev.ID;
            switch (id)
            {
                case Events.MethodEnter:
                {
                    enters++;
                    ulong m = ReadU64(ev, 0);
                    var th = GetThread(threads, ev.ThreadID, ev.TimeStampRelativeMSec);
                    th.Stack.Push(new Frame(m, ev.TimeStampRelativeMSec, 0));
                    break;
                }
                case Events.MethodLeave:
                case Events.MethodTailCall:
                case Events.MethodExceptionLeave:
                {
                    leaves++;
                    ulong m = ReadU64(ev, 0);
                    var th = GetThread(threads, ev.ThreadID, ev.TimeStampRelativeMSec);
                    // Pop until the matching enter (tolerates lost events).
                    while (th.Stack.Count > 0)
                    {
                        var f = th.Stack.Pop();
                        long durNs = (long)((ev.TimeStampRelativeMSec - f.EnterMs) * 1_000_000.0);
                        long selfNs = Math.Max(0, durNs - f.ChildNs);
                        var acc = GetTiming(timings, methods, m == f.MethodId ? m : f.MethodId);
                        acc.Calls++;
                        acc.TotalNs += durNs;
                        acc.SelfNs += selfNs;
                        if (durNs < acc.MinNs) acc.MinNs = durNs;
                        if (durNs > acc.MaxNs) acc.MaxNs = durNs;
                        if (id == Events.MethodExceptionLeave) acc.ExceptionLeaves++;
                        if (th.Stack.Count > 0) th.Stack.Peek().ChildNs += durNs;
                        tree.Record(th.Id, th.Stack, f.MethodId, durNs, selfNs, methods);
                        if (f.MethodId == m) break;
                    }
                    break;
                }
                case Events.GcAllocation:
                {
                    allocs++;
                    ulong vt = ReadU64(ev, 0);
                    long size = (long)ReadU64(ev, 16);
                    var cur = allocByVTable.GetValueOrDefault(vt);
                    allocByVTable[vt] = (cur.count + 1, cur.bytes + size);
                    ulong site = 0;
                    if (threads.TryGetValue(ev.ThreadID, out var th) && th.Stack.Count > 0)
                        site = th.Stack.Peek().MethodId;
                    var sk = (vt, site);
                    var cs = allocBySite.GetValueOrDefault(sk);
                    allocBySite[sk] = (cs.count + 1, cs.bytes + size);
                    break;
                }
                case Events.VTableLoaded:
                    vtableToClass[ReadU64(ev, 0)] = ReadU64(ev, 8);
                    break;
                case Events.ClassLoaded:
                    classNames[ReadU64(ev, 0)] = ReadUnicodeZ(ev, 16);
                    break;
                case Events.GcEvent:
                    gcs++;
                    break;
            }
        };
        src.Process();

        // Resolve names now that the rundown has been read.
        methods.ResolveNames(runtimeNames);

        // Types.
        var types = new List<TypeRecord>();
        var typeIdByVTable = new Dictionary<ulong, int>();
        foreach (var vt in allocByVTable.Keys)
        {
            string name = vtableToClass.TryGetValue(vt, out var cls) && classNames.TryGetValue(cls, out var cn)
                ? cn
                : $"<vtable 0x{vt:X}>";
            typeIdByVTable[vt] = types.Count;
            types.Add(new TypeRecord(types.Count, name, vt, vtableToClass.GetValueOrDefault(vt)));
        }

        var threadRecords = threads.Values
            .Select(t => new ThreadRecord(t.Id, t.Id, null, 0, t.FirstMs, t.LastMs))
            .ToList();

        return new InstrumentingResult(
            src.SessionStartTime,
            src.SessionDuration,
            methods.ToRecords(),
            threadRecords,
            timings.Values.Select(t => new MethodTiming(t.MethodId, t.Calls, t.TotalNs, t.SelfNs, t.Calls == 0 ? 0 : t.MinNs, t.MaxNs, t.ExceptionLeaves)).ToList(),
            tree.ToRecords(),
            types,
            allocByVTable.Select(a => new AllocByType(typeIdByVTable[a.Key], a.Value.count, a.Value.bytes)).ToList(),
            allocBySite.Select(a => new AllocBySite(typeIdByVTable[a.Key.vt], a.Key.method == 0 ? -1 : methods.Intern(a.Key.method), a.Value.count, a.Value.bytes)).ToList(),
            enters, leaves, allocs, gcs);

        static string ModuleName(long moduleId) => "";
    }

    private static ThreadState GetThread(Dictionary<int, ThreadState> threads, int tid, double ms)
    {
        if (!threads.TryGetValue(tid, out var th))
            threads[tid] = th = new ThreadState(tid, ms);
        th.LastMs = ms;
        return th;
    }

    private static TimingAccumulator GetTiming(Dictionary<int, TimingAccumulator> timings, MethodTable methods, ulong runtimeId)
    {
        int id = methods.Intern(runtimeId);
        if (!timings.TryGetValue(id, out var acc))
            timings[id] = acc = new TimingAccumulator(id);
        return acc;
    }

    private static ulong ReadU64(TraceEvent ev, int offset) => (ulong)Marshal.ReadInt64(ev.DataStart, offset);

    private static string ReadUnicodeZ(TraceEvent ev, int offset)
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

    private sealed class Frame
    {
        public Frame(ulong methodId, double enterMs, long childNs) { MethodId = methodId; EnterMs = enterMs; ChildNs = childNs; }
        public ulong MethodId { get; }
        public double EnterMs { get; }
        public long ChildNs;
    }

    private sealed class ThreadState
    {
        public ThreadState(int id, double firstMs) { Id = id; FirstMs = firstMs; LastMs = firstMs; }
        public int Id { get; }
        public double FirstMs { get; }
        public double LastMs;
        public Stack<Frame> Stack { get; } = new();
    }

    private sealed class TimingAccumulator
    {
        public TimingAccumulator(int methodId) { MethodId = methodId; MinNs = long.MaxValue; }
        public int MethodId { get; }
        public long Calls, TotalNs, SelfNs, MinNs, MaxNs, ExceptionLeaves;
    }

    /// <summary>Runtime MethodID -> compact id; names filled in from the rundown at the end.</summary>
    private sealed class MethodTable
    {
        private readonly WaitFrameClassifier _waitFrames;
        private readonly Dictionary<ulong, int> _ids = new();
        private readonly List<ulong> _runtimeIds = new();
        private List<MethodRecord>? _records;

        public MethodTable(WaitFrameClassifier waitFrames) { _waitFrames = waitFrames; }

        public int Intern(ulong runtimeId)
        {
            if (_ids.TryGetValue(runtimeId, out int id)) return id;
            id = _runtimeIds.Count;
            _ids[runtimeId] = id;
            _runtimeIds.Add(runtimeId);
            return id;
        }

        public void ResolveNames(Dictionary<ulong, (string ns, string name, string sig, string module)> names)
        {
            _records = new List<MethodRecord>(_runtimeIds.Count);
            for (int i = 0; i < _runtimeIds.Count; i++)
            {
                ulong rid = _runtimeIds[i];
                if (names.TryGetValue(rid, out var n))
                {
                    // MethodNamespace in rundown events is the full type name (Namespace.Type).
                    string typeFull = n.ns;
                    int dot = typeFull.LastIndexOf('.');
                    string ns = dot < 0 ? "" : typeFull[..dot];
                    string type = dot < 0 ? typeFull : typeFull[(dot + 1)..];
                    string full = $"{typeFull}.{n.name}";
                    _records.Add(new MethodRecord(i, n.module, ns, type, n.name, n.sig, full, rid, _waitFrames.IsWaitFrame(full)));
                }
                else
                {
                    string full = $"<unresolved 0x{rid:X}>";
                    _records.Add(new MethodRecord(i, "", "", "", full, "", full, rid, false));
                }
            }
        }

        public IReadOnlyList<MethodRecord> ToRecords() => _records ?? throw new InvalidOperationException("ResolveNames not called");
    }

    /// <summary>Aggregated instrumented call tree: one root per thread (MethodId -1).</summary>
    private sealed class TreeBuilder
    {
        private sealed class Node
        {
            public int Id, MethodId, ThreadId, Depth;
            public int? ParentId;
            public long Calls, TotalNs, SelfNs;
            public Dictionary<int, Node> Children = new();
        }

        private readonly List<Node> _nodes = new();
        private readonly Dictionary<int, Node> _roots = new();

        /// <summary>Record a completed call of <paramref name="methodId"/> whose (still open) callers are on <paramref name="openStack"/>.</summary>
        public void Record(int threadId, Stack<Frame> openStack, ulong methodId, long totalNs, long selfNs, MethodTable methods)
        {
            if (!_roots.TryGetValue(threadId, out var root))
                _roots[threadId] = root = NewNode(null, -1, threadId, 0);
            var cur = root;
            // Stack enumerates top->bottom; we need bottom->top.
            foreach (var f in openStack.Reverse())
            {
                int m = methods.Intern(f.MethodId);
                if (!cur.Children.TryGetValue(m, out var child))
                    cur.Children[m] = child = NewNode(cur.Id, m, threadId, cur.Depth + 1);
                cur = child;
            }
            int mid = methods.Intern(methodId);
            if (!cur.Children.TryGetValue(mid, out var leaf))
                cur.Children[mid] = leaf = NewNode(cur.Id, mid, threadId, cur.Depth + 1);
            leaf.Calls++;
            leaf.TotalNs += totalNs;
            leaf.SelfNs += selfNs;
        }

        private Node NewNode(int? parent, int method, int thread, int depth)
        {
            var n = new Node { Id = _nodes.Count, ParentId = parent, MethodId = method, ThreadId = thread, Depth = depth };
            _nodes.Add(n);
            return n;
        }

        public IReadOnlyList<TimingTreeNode> ToRecords() =>
            _nodes.Select(n => new TimingTreeNode(n.Id, n.ParentId, n.MethodId, n.ThreadId, n.Depth, n.Calls, n.TotalNs, n.SelfNs)).ToList();
    }
}
