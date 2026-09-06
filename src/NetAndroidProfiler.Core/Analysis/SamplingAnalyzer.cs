using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace NetAndroidProfiler.Core.Analysis;

/// <summary>
/// Turns a sampling .nettrace (Microsoft-DotNETCore-SampleProfiler + rundown)
/// into per-method statistics, an aggregated call tree and caller/callee
/// edges, using TraceLog for stack resolution.
/// </summary>
public sealed class SamplingAnalyzer
{
    private const string SampleProfilerProvider = "Microsoft-DotNETCore-SampleProfiler";

    private readonly WaitFrameClassifier _waitFrames;

    public SamplingAnalyzer(WaitFrameClassifier? waitFrames = null)
    {
        _waitFrames = waitFrames ?? WaitFrameClassifier.Default;
    }

    /// <summary>Analyze <paramref name="netTracePath"/>. The .etlx conversion goes to a private temp file (concurrent analyses of the same trace are safe).</summary>
    public SamplingResult Analyze(string netTracePath, CancellationToken ct = default)
    {
        if (!File.Exists(netTracePath))
            throw new FileNotFoundException("Trace file not found", netTracePath);

        string etlx = Path.Combine(Path.GetTempPath(), $"nap-{Guid.NewGuid():N}.etlx");
        try
        {
            TraceLog.CreateFromEventPipeDataFile(netTracePath, etlx);
            using var log = new TraceLog(etlx);
            return Analyze(log, ct);
        }
        finally
        {
            try { File.Delete(etlx); } catch { /* best effort */ }
        }
    }

    private SamplingResult Analyze(TraceLog log, CancellationToken ct)
    {
        var methods = new MethodTable();
        var threads = new Dictionary<int, ThreadAccumulator>();
        var stats = new Dictionary<int, StatAccumulator>();
        var edges = new Dictionary<(int, int), long>();
        var tree = new TreeBuilder();
        long total = 0, withStack = 0;
        var seen = new HashSet<int>();
        var frames = new List<int>(64);

        foreach (var ev in log.Events)
        {
            if (ev.ProviderName != SampleProfilerProvider) continue;
            ct.ThrowIfCancellationRequested();
            total++;

            if (!threads.TryGetValue(ev.ThreadID, out var th))
                threads[ev.ThreadID] = th = new ThreadAccumulator(ev.ThreadID, ev.Thread()?.ThreadInfo);
            th.Samples++;
            th.Touch(ev.TimeStampRelativeMSec);

            var cs = ev.CallStackIndex();
            if (cs == CallStackIndex.Invalid) continue;
            withStack++;

            // Walk leaf -> root, collect method ids.
            frames.Clear();
            while (cs != CallStackIndex.Invalid)
            {
                frames.Add(methods.Intern(log, log.CallStacks.CodeAddressIndex(cs), _waitFrames));
                cs = log.CallStacks.Caller(cs);
            }

            int leaf = frames[0];
            bool isWait = methods.IsWait(leaf);

            // Exclusive on the leaf; inclusive once per distinct method in the stack.
            seen.Clear();
            for (int i = 0; i < frames.Count; i++)
            {
                int m = frames[i];
                if (!stats.TryGetValue(m, out var st))
                    stats[m] = st = new StatAccumulator(m);
                if (i == 0)
                {
                    st.Exclusive++;
                    if (!isWait) st.ExclusiveCpu++;
                }
                if (seen.Add(m))
                {
                    st.Inclusive++;
                    if (!isWait) st.InclusiveCpu++;
                }
                if (i + 1 < frames.Count)
                {
                    var key = (frames[i + 1], m); // caller -> callee
                    edges[key] = edges.GetValueOrDefault(key) + 1;
                }
            }

            // Root -> leaf path into the aggregated tree.
            tree.AddStack(th.Id, frames, isWait);
        }

        var threadRecords = threads.Values
            .Select(t => new ThreadRecord(t.Id, t.Id, t.Name, t.Samples, t.FirstMs, t.LastMs))
            .OrderByDescending(t => t.Samples)
            .ToList();

        return new SamplingResult(
            log.SessionStartTime,
            log.SessionDuration,
            total,
            withStack,
            methods.ToRecords(),
            threadRecords,
            stats.Values.Select(s => new SampleStat(s.MethodId, s.Inclusive, s.Exclusive, s.InclusiveCpu, s.ExclusiveCpu)).ToList(),
            tree.ToRecords(),
            edges.Select(e => new CallEdge(e.Key.Item1, e.Key.Item2, e.Value)).ToList());
    }

    private sealed class ThreadAccumulator
    {
        public ThreadAccumulator(int id, string? name) { Id = id; Name = name; FirstMs = double.MaxValue; }
        public int Id { get; }
        public string? Name { get; }
        public long Samples;
        public double FirstMs, LastMs;
        public void Touch(double ms) { if (ms < FirstMs) FirstMs = ms; if (ms > LastMs) LastMs = ms; }
    }

    private sealed class StatAccumulator
    {
        public StatAccumulator(int methodId) { MethodId = methodId; }
        public int MethodId { get; }
        public long Inclusive, Exclusive, InclusiveCpu, ExclusiveCpu;
    }

    /// <summary>Interns TraceLog code addresses into compact method ids.</summary>
    internal sealed class MethodTable
    {
        private readonly Dictionary<MethodIndex, int> _byMethod = new();
        private readonly Dictionary<string, int> _byName = new();
        private readonly List<MethodRecord> _records = new();

        public int Intern(TraceLog log, CodeAddressIndex ca, WaitFrameClassifier waitFrames)
        {
            var mi = log.CodeAddresses.MethodIndex(ca);
            if (mi != MethodIndex.Invalid)
            {
                if (_byMethod.TryGetValue(mi, out int id)) return id;
                var m = log.CodeAddresses.Methods;
                string full = m.FullMethodName(mi);
                var mfi = m.MethodModuleFileIndex(mi);
                string module = mfi != ModuleFileIndex.Invalid ? log.ModuleFiles[mfi].Name : "";
                id = Add(module, full, (ulong)m.MethodRva(mi), m.MethodToken(mi), waitFrames);
                _byMethod[mi] = id;
                return id;
            }
            var mod = log.CodeAddresses.ModuleFile(ca);
            string key = $"?{mod?.Name ?? "unknown"}!0x{log.CodeAddresses.Address(ca):X}";
            if (_byName.TryGetValue(key, out int uid)) return uid;
            uid = Add(mod?.Name ?? "", key, log.CodeAddresses.Address(ca), 0, waitFrames);
            _byName[key] = uid;
            return uid;
        }

        public bool IsWait(int id) => _records[id].IsWaitFrame;

        private int Add(string module, string fullName, ulong runtimeId, int token, WaitFrameClassifier waitFrames)
        {
            int id = _records.Count;
            SplitName(fullName, out string ns, out string type, out string name, out string sig);
            _records.Add(new MethodRecord(id, module, ns, type, name, sig, fullName, runtimeId, waitFrames.IsWaitFrame(fullName), token));
            return id;
        }

        public IReadOnlyList<MethodRecord> ToRecords() => _records;

        /// <summary>Splits "Ns.Sub.Type.Method(sig)" into namespace / type / method / signature (best effort; generics keep their brackets).</summary>
        internal static void SplitName(string full, out string ns, out string type, out string name, out string sig)
        {
            sig = "";
            string head = full;
            int paren = IndexOfTopLevel(full, '(');
            if (paren >= 0) { sig = full[paren..]; head = full[..paren]; }
            int dot = LastTopLevelDot(head);
            if (dot < 0) { ns = ""; type = ""; name = head; return; }
            name = head[(dot + 1)..];
            string typeFull = head[..dot];
            int dot2 = LastTopLevelDot(typeFull);
            if (dot2 < 0) { ns = ""; type = typeFull; return; }
            ns = typeFull[..dot2];
            type = typeFull[(dot2 + 1)..];
        }

        private static int IndexOfTopLevel(string s, char c)
        {
            int depth = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch == '<' || ch == '[') depth++;
                else if (ch == '>' || ch == ']') depth--;
                else if (ch == c && depth == 0) return i;
            }
            return -1;
        }

        private static int LastTopLevelDot(string s)
        {
            int depth = 0;
            for (int i = s.Length - 1; i >= 0; i--)
            {
                char ch = s[i];
                if (ch == '>' || ch == ']') depth++;
                else if (ch == '<' || ch == '[') depth--;
                else if (ch == '.' && depth == 0) return i;
            }
            return -1;
        }
    }

    /// <summary>Aggregated call tree: one root per thread, children keyed by method id.</summary>
    private sealed class TreeBuilder
    {
        private sealed class Node
        {
            public int Id, MethodId, ThreadId, Depth;
            public int? ParentId;
            public long Inclusive, Exclusive, InclusiveCpu, ExclusiveCpu;
            public Dictionary<int, Node> Children = new();
        }

        private readonly List<Node> _nodes = new();
        private readonly Dictionary<int, Node> _roots = new();

        public void AddStack(int threadId, List<int> leafToRoot, bool isWait)
        {
            // Thread root node uses MethodId -1.
            if (!_roots.TryGetValue(threadId, out var root))
                _roots[threadId] = root = NewNode(null, -1, threadId, 0);
            root.Inclusive++; if (!isWait) root.InclusiveCpu++;
            var cur = root;
            for (int i = leafToRoot.Count - 1; i >= 0; i--)
            {
                int m = leafToRoot[i];
                if (!cur.Children.TryGetValue(m, out var child))
                    cur.Children[m] = child = NewNode(cur.Id, m, threadId, cur.Depth + 1);
                child.Inclusive++; if (!isWait) child.InclusiveCpu++;
                cur = child;
            }
            cur.Exclusive++; if (!isWait) cur.ExclusiveCpu++;
        }

        private Node NewNode(int? parent, int method, int thread, int depth)
        {
            var n = new Node { Id = _nodes.Count, ParentId = parent, MethodId = method, ThreadId = thread, Depth = depth };
            _nodes.Add(n);
            return n;
        }

        public IReadOnlyList<SampleTreeNode> ToRecords() =>
            _nodes.Select(n => new SampleTreeNode(n.Id, n.ParentId, n.MethodId, n.ThreadId, n.Depth, n.Inclusive, n.Exclusive, n.InclusiveCpu, n.ExclusiveCpu)).ToList();
    }
}
