namespace NetAndroidProfiler.Core.Analysis;

/// <summary>A managed method seen in a trace. Ids are local to one analysis result / session database.</summary>
public sealed record MethodRecord(
    int Id,
    string Module,
    string Namespace,
    string TypeName,
    string Name,
    string Signature,
    string FullName,
    ulong RuntimeMethodId,
    bool IsWaitFrame,
    int Token = 0);

/// <summary>A thread seen in a trace.</summary>
public sealed record ThreadRecord(int Id, int OsThreadId, string? Name, long Samples, double FirstMs, double LastMs);

/// <summary>Per-method sampling statistics. Counts are sample counts; *Cpu variants exclude samples whose leaf frame is a wait frame.</summary>
public sealed record SampleStat(int MethodId, long Inclusive, long Exclusive, long InclusiveCpu, long ExclusiveCpu);

/// <summary>Aggregated call-tree node (same method under the same parent is merged). Counts are samples.</summary>
public sealed record SampleTreeNode(
    int Id,
    int? ParentId,
    int MethodId,
    int ThreadId,
    int Depth,
    long Inclusive,
    long Exclusive,
    long InclusiveCpu,
    long ExclusiveCpu);

/// <summary>Caller -> callee edge with the number of samples in which it appears.</summary>
public sealed record CallEdge(int CallerMethodId, int CalleeMethodId, long Samples);

/// <summary>Result of analyzing a sampling trace.</summary>
public sealed record SamplingResult(
    DateTimeOffset SessionStart,
    TimeSpan Duration,
    long TotalSamples,
    long SamplesWithStack,
    IReadOnlyList<MethodRecord> Methods,
    IReadOnlyList<ThreadRecord> Threads,
    IReadOnlyList<SampleStat> Stats,
    IReadOnlyList<SampleTreeNode> Tree,
    IReadOnlyList<CallEdge> Edges)
{
    public MethodRecord Method(int id) => Methods[id];
}

/// <summary>Per-method timing from enter/leave instrumentation.</summary>
public sealed record MethodTiming(int MethodId, long Calls, long TotalNs, long SelfNs, long MinNs, long MaxNs, long ExceptionLeaves);

/// <summary>Aggregated timing call-tree node (instrumented frames only).</summary>
public sealed record TimingTreeNode(int Id, int? ParentId, int MethodId, int ThreadId, int Depth, long Calls, long TotalNs, long SelfNs);

/// <summary>A managed type seen in allocation events.</summary>
public sealed record TypeRecord(int Id, string Name, ulong VTableId, ulong ClassId);

/// <summary>Allocations aggregated per type.</summary>
public sealed record AllocByType(int TypeId, long Count, long Bytes);

/// <summary>Allocations aggregated per (type, innermost instrumented method on the allocating thread). MethodId -1 = no instrumented frame open.</summary>
public sealed record AllocBySite(int TypeId, int MethodId, long Count, long Bytes);

/// <summary>Result of analyzing a Microsoft-DotNETRuntimeMonoProfiler trace.</summary>
public sealed record InstrumentingResult(
    DateTimeOffset SessionStart,
    TimeSpan Duration,
    IReadOnlyList<MethodRecord> Methods,
    IReadOnlyList<ThreadRecord> Threads,
    IReadOnlyList<MethodTiming> Timings,
    IReadOnlyList<TimingTreeNode> Tree,
    IReadOnlyList<TypeRecord> Types,
    IReadOnlyList<AllocByType> AllocsByType,
    IReadOnlyList<AllocBySite> AllocsBySite,
    long EnterEvents,
    long LeaveEvents,
    long AllocationEvents,
    long GcEvents)
{
    public MethodRecord Method(int id) => Methods[id];
}
