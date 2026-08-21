using NetAndroidProfiler.Core.Analysis;

namespace NetAndroidProfiler.Core.Weaving;

/// <summary>
/// Turns the .napw event files written by NetAndroidProfiler.Collector plus
/// the weaver's id map into the same <see cref="InstrumentingResult"/> the
/// runtime-provider analyzer produces (timings, aggregated timing tree), so
/// the store and every frontend treat both instrumenting engines identically.
/// File format: see NetAndroidProfiler.Collector.Profiler.
/// </summary>
public sealed class WeaveAnalyzer
{
    private const int HeaderSize = 4 + 1 + 8 + 4 + 4;
    private const int RecordSize = 13;

    /// <summary>Analyze every *.napw file in <paramref name="directory"/> with the given id map.</summary>
    public InstrumentingResult Analyze(string directory, IReadOnlyList<WovenMethod> map, CancellationToken ct = default)
    {
        var files = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.napw") : [];
        if (files.Length == 0)
            throw new FileNotFoundException($"No .napw event files in {directory}");

        // Method table: ids come from the weaver map (dense, 1-based); build 0-based records.
        var idToIndex = new Dictionary<int, int>();
        var methods = new List<MethodRecord>();
        foreach (var m in map)
        {
            idToIndex[m.Id] = methods.Count;
            int dot = m.FullName.LastIndexOf('.');
            string typeFull = dot < 0 ? "" : m.FullName[..dot];
            string name = dot < 0 ? m.FullName : m.FullName[(dot + 1)..];
            int dot2 = typeFull.LastIndexOf('.');
            methods.Add(new MethodRecord(methods.Count, m.Module, dot2 < 0 ? "" : typeFull[..dot2], dot2 < 0 ? typeFull : typeFull[(dot2 + 1)..], name, "", m.FullName, 0, false, m.Token));
        }
        int Unknown(int weaveId)
        {
            if (idToIndex.TryGetValue(weaveId, out int i)) return i;
            int idx = methods.Count;
            methods.Add(new MethodRecord(idx, "", "", "", $"<unknown weave id {weaveId}>", "", $"<unknown weave id {weaveId}>", 0, false, 0));
            idToIndex[weaveId] = idx;
            return idx;
        }

        // Allocation type names, written by the collector next to the event files.
        var typeNames = new Dictionary<int, string>();
        string typesFile = Path.Combine(directory, "nap-types.txt");
        if (File.Exists(typesFile))
        {
            foreach (var line in File.ReadAllLines(typesFile))
            {
                int tab = line.IndexOf('	');
                if (tab > 0 && int.TryParse(line[..tab], out int tid)) typeNames[tid] = line[(tab + 1)..];
            }
        }
        var typeIndex = new Dictionary<int, int>();
        var types = new List<TypeRecord>();
        var allocByType = new Dictionary<int, long>();
        var allocBySite = new Dictionary<(int type, int method), long>();
        long allocEvents = 0;

        var timings = new Dictionary<int, Acc>();
        var tree = new TreeBuilder();
        var threads = new List<ThreadRecord>();
        long enters = 0, leaves = 0, brokenPairs = 0;
        DateTimeOffset start = DateTimeOffset.MinValue;
        double maxMs = 0;

        foreach (var file in files.OrderBy(f => f))
        {
            ct.ThrowIfCancellationRequested();
            // The collector may still hold the file open for writing (in-process tests): share ReadWrite.
            byte[] data;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                data = new byte[fs.Length];
                int read = 0;
                while (read < data.Length)
                {
                    int n = fs.Read(data, read, data.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
            }
            if (data.Length < HeaderSize || data[0] != 'N' || data[1] != 'A' || data[2] != 'P' || data[3] != 'W')
                continue;
            byte version = data[4];
            if (version != 1) throw new InvalidDataException($"Unsupported .napw version {version} in {file}");
            long freq = ReadI64(data, 5);
            int threadId = ReadI32(data, 17);
            double ticksToMs = 1000.0 / freq;

            var stack = new Stack<(int idx, long ticks, long childTicks)>();
            long first = 0, last = 0;
            int usable = (data.Length - HeaderSize) / RecordSize;
            for (int r = 0; r < usable; r++)
            {
                int o = HeaderSize + r * RecordSize;
                byte kind = data[o];
                int weaveId = ReadI32(data, o + 1);
                long ticks = ReadI64(data, o + 5);
                if (first == 0) first = ticks;
                last = ticks;
                int idx = kind == 4 ? -1 : Unknown(weaveId);
                switch (kind)
                {
                    case 1:
                        enters++;
                        stack.Push((idx, ticks, 0));
                        break;
                    case 4:
                    {
                        allocEvents++;
                        if (!typeIndex.TryGetValue(weaveId, out int ti))
                        {
                            ti = types.Count;
                            typeIndex[weaveId] = ti;
                            types.Add(new TypeRecord(ti, typeNames.TryGetValue(weaveId, out var tn) ? tn : $"<type {weaveId}>", 0, 0));
                        }
                        allocByType[ti] = allocByType.GetValueOrDefault(ti) + 1;
                        int site = stack.Count > 0 ? stack.Peek().idx : -1;
                        var key = (ti, site);
                        allocBySite[key] = allocBySite.GetValueOrDefault(key) + 1;
                        break;
                    }
                    case 2:
                    case 3:
                        leaves++;
                        while (stack.Count > 0)
                        {
                            var f = stack.Pop();
                            long durTicks = ticks - f.ticks;
                            // A leave that predates its enter means the record stream was cut
                            // (files cleared under a running collector, or a truncated pull).
                            // Dropping the pair is the only honest answer: a negative duration
                            // would poison the totals for the whole method.
                            if (durTicks < 0)
                            {
                                brokenPairs++;
                                if (f.idx == idx) break;
                                continue;
                            }
                            long selfTicks = Math.Max(0, durTicks - f.childTicks);
                            long durNs = (long)(durTicks * ticksToMs * 1_000_000.0); // ms -> ns
                            long selfNs = (long)(selfTicks * ticksToMs * 1_000_000.0);
                            if (!timings.TryGetValue(f.idx, out var acc)) timings[f.idx] = acc = new Acc(f.idx);
                            acc.Calls++;
                            acc.TotalNs += durNs;
                            acc.SelfNs += selfNs;
                            if (durNs < acc.MinNs) acc.MinNs = durNs;
                            if (durNs > acc.MaxNs) acc.MaxNs = durNs;
                            if (kind == 3) acc.ExceptionLeaves++;
                            if (stack.Count > 0)
                            {
                                var parent = stack.Pop();
                                stack.Push((parent.idx, parent.ticks, parent.childTicks + durTicks));
                            }
                            tree.Record(threadId, stack, f.idx, durNs, selfNs);
                            if (f.idx == idx) break;
                        }
                        break;
                }
            }
            double firstMs = first * ticksToMs, lastMs = last * ticksToMs;
            threads.Add(new ThreadRecord(threadId, threadId, null, 0, firstMs, lastMs));
            if (lastMs - firstMs > maxMs) maxMs = lastMs - firstMs;
        }

        return new InstrumentingResult(
            start,
            TimeSpan.FromMilliseconds(maxMs),
            methods,
            threads,
            timings.Values.Select(t => new MethodTiming(t.MethodId, t.Calls, t.TotalNs, t.SelfNs, t.Calls == 0 ? 0 : t.MinNs, t.MaxNs, t.ExceptionLeaves)).ToList(),
            tree.ToRecords(),
            types,
            allocByType.Select(a => new AllocByType(a.Key, a.Value, 0)).ToList(),
            allocBySite.Select(a => new AllocBySite(a.Key.type, a.Key.method, a.Value, 0)).ToList(),
            enters, leaves, allocEvents, 0, brokenPairs);
    }

    private static int ReadI32(byte[] b, int o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);

    private static long ReadI64(byte[] b, int o)
    {
        long v = 0;
        for (int i = 7; i >= 0; i--) v = (v << 8) | b[o + i];
        return v;
    }

    private sealed class Acc
    {
        public Acc(int methodId) { MethodId = methodId; MinNs = long.MaxValue; }
        public int MethodId { get; }
        public long Calls, TotalNs, SelfNs, MinNs, MaxNs, ExceptionLeaves;
    }

    /// <summary>Aggregated timing tree (same shape as the runtime-provider analyzer's).</summary>
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

        public void Record(int threadId, Stack<(int idx, long ticks, long childTicks)> openStack, int methodIdx, long totalNs, long selfNs)
        {
            if (!_roots.TryGetValue(threadId, out var root))
                _roots[threadId] = root = NewNode(null, -1, threadId, 0);
            var cur = root;
            foreach (var f in openStack.Reverse())
            {
                if (!cur.Children.TryGetValue(f.idx, out var child))
                    cur.Children[f.idx] = child = NewNode(cur.Id, f.idx, threadId, cur.Depth + 1);
                cur = child;
            }
            if (!cur.Children.TryGetValue(methodIdx, out var leaf))
                cur.Children[methodIdx] = leaf = NewNode(cur.Id, methodIdx, threadId, cur.Depth + 1);
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
