using System.Text;
using NetAndroidProfiler.Core.Sessions;
using NetAndroidProfiler.Core.Store;

namespace NetAndroidProfiler.Mcp;

/// <summary>Compact plain-text rendering of Core results for the model.</summary>
internal static class TextFormat
{
    public static string Info(SessionInfo i)
    {
        var sb = new StringBuilder();
        sb.Append($"session={i.Id} state={i.State} mode={i.Spec.Mode} device={i.Spec.DeviceSerial} package={i.Spec.Package} launch={i.Spec.Launch}");
        if (i.StartedUtc is not null && i.EndedUtc is not null) sb.Append($" duration={(i.EndedUtc.Value - i.StartedUtc.Value).TotalSeconds:F1}s");
        sb.AppendLine();
        sb.AppendLine($"  directory={i.Directory}");
        if (i.Error is not null) sb.AppendLine("  error: " + i.Error);
        foreach (var w in i.Warnings) sb.AppendLine("  warning: " + w);
        return sb.ToString().TrimEnd();
    }

    public static string Hotspots(IReadOnlyList<HotMethodRow> rows, long? totalWithStack, bool exclusive, bool cpuOnly)
    {
        if (rows.Count == 0) return "No samples.";
        var sb = new StringBuilder();
        sb.AppendLine($"{"id",5} {"incl",8} {"excl",8} {"incl_cpu",8} {"excl_cpu",8} {(cpuOnly ? "%cpu" : "%"),6}  method   (ordered by {(exclusive ? "exclusive" : "inclusive")}{(cpuOnly ? "_cpu" : "")})");
        foreach (var r in rows)
        {
            long v = exclusive ? (cpuOnly ? r.ExclusiveCpu : r.Exclusive) : (cpuOnly ? r.InclusiveCpu : r.Inclusive);
            string pct = totalWithStack is > 0 ? $"{100.0 * v / totalWithStack.Value,5:F1}%" : "";
            sb.AppendLine($"{r.MethodId,5} {r.Inclusive,8} {r.Exclusive,8} {r.InclusiveCpu,8} {r.ExclusiveCpu,8} {pct,6}  {r.FullName}{(r.IsWaitFrame ? "  [wait]" : "")}");
        }
        return sb.ToString().TrimEnd();
    }

    public static string Tree(IReadOnlyList<TreeRow> rows, bool timing)
    {
        if (rows.Count == 0) return "No children.";
        var sb = new StringBuilder();
        sb.AppendLine(timing
            ? $"{"node",6} {"depth",5} {"calls",9} {"total_ms",10} {"self_ms",10}  method"
            : $"{"node",6} {"depth",5} {"incl",8} {"excl",8} {"incl_cpu",8} {"excl_cpu",8}  method");
        foreach (var r in rows)
        {
            string more = r.HasChildren ? " +" : "";
            sb.AppendLine(timing
                ? $"{r.Id,6} {r.Depth,5} {r.Calls,9} {r.Value1 / 1e6,10:F3} {r.Value2 / 1e6,10:F3}  {r.FullName}{more}"
                : $"{r.Id,6} {r.Depth,5} {r.Value1,8} {r.Value2,8} {r.Value1Cpu,8} {r.Value2Cpu,8}  {r.FullName}{more}");
        }
        sb.AppendLine("(+ = has children; pass nodeId to profile_tree to expand)");
        return sb.ToString().TrimEnd();
    }

    public static string Edges(string title, IReadOnlyList<EdgeRow> rows)
    {
        if (rows.Count == 0) return $"{title}: none.";
        var sb = new StringBuilder(title + ":\n");
        foreach (var r in rows) sb.AppendLine($"{r.MethodId,5} {r.Samples,8}  {r.FullName}");
        return sb.ToString().TrimEnd();
    }

    public static string Timings(IReadOnlyList<TimingRow> rows)
    {
        if (rows.Count == 0) return "No instrumented methods (check the callspec).";
        var sb = new StringBuilder();
        sb.AppendLine($"{"id",5} {"calls",9} {"total_ms",11} {"self_ms",11} {"avg_us",9} {"min_us",9} {"max_us",9} {"exc",5}  method");
        foreach (var r in rows)
            sb.AppendLine($"{r.MethodId,5} {r.Calls,9} {r.TotalNs / 1e6,11:F3} {r.SelfNs / 1e6,11:F3} {(r.Calls == 0 ? 0 : r.TotalNs / 1e3 / r.Calls),9:F1} {r.MinNs / 1e3,9:F1} {r.MaxNs / 1e3,9:F1} {r.ExceptionLeaves,5}  {r.FullName}");
        return sb.ToString().TrimEnd();
    }

    public static string AllocTypes(IReadOnlyList<AllocTypeRow> rows)
    {
        if (rows.Count == 0)
            return "No allocation events. A session woven at runtime records them with trackAllocations=true; " +
                   "one that uses a build-time weave map records what the build wove, so allocations need " +
                   "the weaver's --allocations in that build (-p:NapWeaveArgs=--allocations).";
        var sb = new StringBuilder();
        sb.AppendLine($"{"type",5} {"count",10} {"bytes",12} {"avg",7}  type name");
        foreach (var r in rows) sb.AppendLine($"{r.TypeId,5} {r.Count,10} {r.Bytes,12} {(r.Count == 0 ? 0 : r.Bytes / r.Count),7}  {r.TypeName}");
        return sb.ToString().TrimEnd();
    }

    public static string AllocSites(IReadOnlyList<AllocSiteRow> rows)
    {
        if (rows.Count == 0) return "No allocation sites.";
        var sb = new StringBuilder();
        sb.AppendLine($"{"count",10} {"bytes",12}  type  <-  allocating instrumented method");
        foreach (var r in rows) sb.AppendLine($"{r.Count,10} {r.Bytes,12}  {r.TypeName}  <-  {r.MethodFullName}");
        return sb.ToString().TrimEnd();
    }

    public static string Heap(IReadOnlyList<(int snapshotId, string typeName, long count, long bytes)> rows)
    {
        if (rows.Count == 0) return "No heap snapshot rows.";
        var sb = new StringBuilder();
        sb.AppendLine($"{"count",10} {"bytes",12}  type");
        foreach (var r in rows) sb.AppendLine($"{r.count,10} {r.bytes,12}  {r.typeName}");
        return sb.ToString().TrimEnd();
    }

    public static string HeapDiff(IReadOnlyList<(int id, DateTimeOffset takenUtc, long objects, long bytes)> snapshots, IReadOnlyList<HeapDiffRow> rows)
    {
        var sb = new StringBuilder();
        foreach (var s in snapshots)
            sb.AppendLine($"snapshot {s.id}: {s.objects} objects, {s.bytes} bytes ({s.takenUtc:HH:mm:ss} UTC)");
        if (rows.Count == 0) return sb.Append("No types in common.").ToString();
        sb.AppendLine();
        sb.AppendLine($"{"count1",10} {"count2",10} {"dCount",10} {"bytes1",12} {"bytes2",12} {"dBytes",12}  type");
        foreach (var r in rows)
            sb.AppendLine($"{r.CountFrom,10} {r.CountTo,10} {r.DeltaCount,10} {r.BytesFrom,12} {r.BytesTo,12} {r.DeltaBytes,12}  {r.TypeName}");
        return sb.ToString().TrimEnd();
    }

    public static string Threads(IReadOnlyList<ThreadRow> rows)
    {
        var sb = new StringBuilder();
        foreach (var t in rows) sb.AppendLine($"thread {t.Id} samples={t.Samples} {(t.Name is null ? "" : "name=" + t.Name)} [{t.FirstMs:F0}..{t.LastMs:F0} ms]");
        return sb.Length == 0 ? "No threads." : sb.ToString().TrimEnd();
    }
}
