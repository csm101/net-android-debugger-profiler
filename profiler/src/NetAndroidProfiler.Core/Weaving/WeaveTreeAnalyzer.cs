using NetAndroidProfiler.Core.Analysis;

namespace NetAndroidProfiler.Core.Weaving;

/// <summary>
/// Reads the calling context trees the collector keeps in the app (*.napt) and turns them
/// into the same <see cref="InstrumentingResult"/> the event stream produces.
///
/// There is nothing to replay here: the app already did the aggregation, one node per call
/// path, so this is a rename of counters into records. What the result cannot carry is what
/// the app never kept - the order calls happened in, and the duration of any single call
/// beyond the minimum and the maximum.
///
/// File format, written whole on every flush (see NetAndroidProfiler.Collector.Profiler):
///   header: "NAPT" 0x01, i64 Stopwatch.Frequency, i32 threadId, i32 nodeCount
///   node:   i32 parentIndex (-1 at the root), i32 methodId,
///           i64 calls, i64 inclusiveTicks, i64 exclusiveTicks, i64 minTicks, i64 maxTicks
/// </summary>
public sealed class WeaveTreeAnalyzer
{
    /// <summary>True when the directory holds trees rather than an event stream.</summary>
    public static bool HasTrees(string directory) =>
        Directory.Exists(directory) && Directory.GetFiles(directory, "*.napt").Length > 0;

    public InstrumentingResult Analyze(string directory, IReadOnlyList<WovenMethod> map, CancellationToken ct = default)
    {
        var files = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.napt") : [];
        if (files.Length == 0)
            throw new FileNotFoundException($"No .napt call trees in {directory}");

        var idToIndex = new Dictionary<int, int>();
        var methods = new List<MethodRecord>();
        foreach (var m in map)
        {
            idToIndex[m.Id] = methods.Count;
            int dot = m.FullName.LastIndexOf('.');
            string typeFull = dot < 0 ? "" : m.FullName[..dot];
            string name = dot < 0 ? m.FullName : m.FullName[(dot + 1)..];
            int dot2 = typeFull.LastIndexOf('.');
            methods.Add(new MethodRecord(methods.Count, m.Module, dot2 < 0 ? "" : typeFull[..dot2],
                dot2 < 0 ? typeFull : typeFull[(dot2 + 1)..], name, "", m.FullName, 0, false, m.Token));
        }

        int Unknown(int weaveId)
        {
            if (idToIndex.TryGetValue(weaveId, out int i)) return i;
            int index = methods.Count;
            methods.Add(new MethodRecord(index, "", "", "", $"<unknown weave id {weaveId}>", "",
                $"<unknown weave id {weaveId}>", 0, false, 0));
            idToIndex[weaveId] = index;
            return index;
        }

        var timings = new Dictionary<int, (long Calls, long TotalNs, long SelfNs, long MinNs, long MaxNs)>();
        var treeNodes = new List<TimingTreeNode>();
        var threads = new List<ThreadRecord>();
        long enterEvents = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(file);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 21) continue;
            if (reader.ReadByte() != 'N' || reader.ReadByte() != 'A' || reader.ReadByte() != 'P' || reader.ReadByte() != 'T') continue;
            if (reader.ReadByte() != 1) continue;
            long frequency = reader.ReadInt64();
            int threadId = reader.ReadInt32();
            int count = reader.ReadInt32();
            if (count <= 0) continue;
            double ticksToNs = 1_000_000_000.0 / frequency;

            // Node index in the file -> id in the result, so parents resolve across threads.
            var ids = new int[count];
            var depths = new int[count];
            long threadInclusive = 0;

            for (int i = 0; i < count; i++)
            {
                int parent = reader.ReadInt32();
                int methodId = reader.ReadInt32();
                long calls = reader.ReadInt64();
                long inclusiveTicks = reader.ReadInt64();
                long exclusiveTicks = reader.ReadInt64();
                long minTicks = reader.ReadInt64();
                long maxTicks = reader.ReadInt64();

                int method = Unknown(methodId);
                long totalNs = (long)(inclusiveTicks * ticksToNs);
                long selfNs = (long)(exclusiveTicks * ticksToNs);

                ids[i] = treeNodes.Count;
                depths[i] = parent >= 0 && parent < i ? depths[parent] + 1 : 0;
                treeNodes.Add(new TimingTreeNode(ids[i], parent >= 0 && parent < i ? ids[parent] : null,
                    method, threadId, depths[i], calls, totalNs, selfNs));

                var current = timings.TryGetValue(method, out var t) ? t : (0, 0, 0, 0, 0);
                timings[method] = (
                    current.Item1 + calls,
                    current.Item2 + totalNs,
                    current.Item3 + selfNs,
                    Min(current.Item4, (long)(minTicks * ticksToNs)),
                    Math.Max(current.Item5, (long)(maxTicks * ticksToNs)));

                enterEvents += calls;
                if (depths[i] == 0) threadInclusive += totalNs;
            }

            threads.Add(new ThreadRecord(threadId, threadId, null, 0, 0, threadInclusive / 1_000_000.0));
        }

        // Allocations stay events even in this mode - one record each, in the .napw stream -
        // because their volume is the user's choice (trackAllocations) rather than the
        // profiler's. Their type is known; which method allocated is not, since that came
        // from replaying enter/leave, and nothing is replayed here.
        var types = new List<TypeRecord>();
        var allocByType = new Dictionary<int, long>();
        ReadAllocations(directory, types, allocByType, ct);

        double durationMs = threads.Count == 0 ? 0 : threads.Max(t => t.LastMs);
        return new InstrumentingResult(
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(durationMs),
            methods,
            threads,
            timings.Select(t => new MethodTiming(t.Key, t.Value.Calls, t.Value.TotalNs, t.Value.SelfNs, t.Value.MinNs, t.Value.MaxNs, 0)).ToList(),
            treeNodes,
            types,
            allocByType.Select(a => new AllocByType(a.Key, a.Value, 0)).ToList(),
            [],
            enterEvents, enterEvents, allocByType.Values.Sum(), 0);
    }

    /// <summary>
    /// Allocation records (kind 4) out of the event stream, with the type names the
    /// collector wrote beside them. Everything else in those files belongs to trace mode
    /// and is ignored here.
    /// </summary>
    private static void ReadAllocations(string directory, List<TypeRecord> types,
        Dictionary<int, long> allocByType, CancellationToken ct)
    {
        const int headerSize = 4 + 1 + 8 + 4 + 4, recordSize = 13;

        var typeNames = new Dictionary<int, string>();
        string typesFile = Path.Combine(directory, "nap-types.txt");
        if (File.Exists(typesFile))
            foreach (var line in File.ReadAllLines(typesFile))
            {
                int tab = line.IndexOf('	');
                if (tab > 0 && int.TryParse(line[..tab], out int id)) typeNames[id] = line[(tab + 1)..];
            }

        var typeIndex = new Dictionary<int, int>();
        foreach (var file in Directory.GetFiles(directory, "*.napw"))
        {
            ct.ThrowIfCancellationRequested();
            byte[] data = File.ReadAllBytes(file);
            if (data.Length < headerSize) continue;
            int usable = (data.Length - headerSize) / recordSize;
            for (int r = 0; r < usable; r++)
            {
                int offset = headerSize + r * recordSize;
                if (data[offset] != 4) continue;
                int typeId = BitConverter.ToInt32(data, offset + 1);
                if (!typeIndex.TryGetValue(typeId, out int index))
                {
                    index = types.Count;
                    typeIndex[typeId] = index;
                    types.Add(new TypeRecord(index, typeNames.TryGetValue(typeId, out var name) ? name : $"<type {typeId}>", 0, 0));
                }
                allocByType[index] = allocByType.GetValueOrDefault(index) + 1;
            }
        }
    }

    /// <summary>Zero means "nothing seen yet", so it must not win a minimum.</summary>
    private static long Min(long current, long candidate) =>
        current == 0 ? candidate : (candidate == 0 ? current : Math.Min(current, candidate));
}
