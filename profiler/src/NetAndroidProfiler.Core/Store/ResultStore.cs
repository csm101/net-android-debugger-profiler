using Microsoft.Data.Sqlite;
using NetAndroidProfiler.Core.Analysis;

namespace NetAndroidProfiler.Core.Store;

/// <summary>Row describing the session (one per database).</summary>
public sealed record SessionRow(
    string Id,
    string Mode,
    string State,
    string? Package,
    string? DeviceSerial,
    DateTimeOffset? StartedUtc,
    double? DurationMs,
    string? TraceFile,
    long? TotalSamples,
    long? SamplesWithStack,
    string? SpecJson,
    string? Error);

/// <summary>Hot-list row (sampling).</summary>
public sealed record HotMethodRow(int MethodId, string FullName, string Module, long Inclusive, long Exclusive, long InclusiveCpu, long ExclusiveCpu, bool IsWaitFrame);

/// <summary>Call-tree row (sampling or timing; Value1/Value2 = inclusive/exclusive samples or total/self ns).</summary>
public sealed record TreeRow(int Id, int? ParentId, int MethodId, string FullName, int ThreadId, int Depth, long Calls, long Value1, long Value2, long Value1Cpu, long Value2Cpu, bool HasChildren);

/// <summary>Caller or callee row.</summary>
public sealed record EdgeRow(int MethodId, string FullName, long Samples);

/// <summary>Timing row (instrumenting).</summary>
public sealed record TimingRow(int MethodId, string FullName, long Calls, long TotalNs, long SelfNs, long MinNs, long MaxNs, long ExceptionLeaves);

/// <summary>Allocation-by-type row.</summary>
public sealed record AllocTypeRow(int TypeId, string TypeName, long Count, long Bytes);

/// <summary>Allocation-by-site row.</summary>
public sealed record AllocSiteRow(int TypeId, string TypeName, int MethodId, string MethodFullName, long Count, long Bytes);

/// <summary>Thread row.</summary>
public sealed record ThreadRow(int Id, int OsTid, string? Name, long Samples, double? FirstMs, double? LastMs);

/// <summary>One type compared between two heap snapshots.</summary>
public sealed record HeapDiffRow(string TypeName, long CountFrom, long CountTo, long BytesFrom, long BytesTo)
{
    public long DeltaCount => CountTo - CountFrom;
    public long DeltaBytes => BytesTo - BytesFrom;
}

/// <summary>Per-method figures of either kind, for source annotation (zeros when the kind was not collected).</summary>
public sealed record MethodFigures(int MethodId, int Token, string FullName, long Inclusive, long Exclusive, long InclusiveCpu, long ExclusiveCpu, long Calls, long TotalNs, long SelfNs);

/// <summary>
/// Writer/reader for a session database (SQLite). One database per session;
/// schema in <see cref="ResultSchema"/>.
/// </summary>
public sealed class ResultStore : IDisposable
{
    private readonly SqliteConnection _conn;

    private ResultStore(SqliteConnection conn) { _conn = conn; }

    public string DataSource => _conn.DataSource;

    /// <summary>Create a new database (fails if the file exists).</summary>
    public static ResultStore Create(string path, string toolVersion)
    {
        if (File.Exists(path))
            throw new IOException($"Database already exists: {path}");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        conn.Open();
        using (var tx = conn.BeginTransaction())
        {
            Exec(conn, ResultSchema.CreateScript);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO schema_info(version, created_utc, tool_version) VALUES ($v, $c, $t)";
            cmd.Parameters.AddWithValue("$v", ResultSchema.Version);
            cmd.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$t", toolVersion);
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
        return new ResultStore(conn);
    }

    /// <summary>Open an existing database; verifies the schema version.</summary>
    public static ResultStore Open(string path, bool readOnly = true)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Session database not found", path);
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite }.ToString());
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT version FROM schema_info";
            var v = Convert.ToInt32(cmd.ExecuteScalar());
            if (v != ResultSchema.Version)
            {
                conn.Dispose();
                throw new InvalidDataException($"Unsupported session database schema version {v} (expected {ResultSchema.Version}): {path}");
            }
        }
        return new ResultStore(conn);
    }

    public void Dispose() => _conn.Dispose();

    // ------------------------------------------------------------------ write

    public void WriteSession(SessionRow s)
    {
        using var tx = _conn.BeginTransaction();
        Exec(_conn, "DELETE FROM session");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO session(id, mode, state, package, device_serial, started_utc, duration_ms, trace_file, total_samples, samples_with_stack, spec_json, error)
            VALUES ($id, $mode, $state, $package, $serial, $started, $dur, $trace, $total, $withstack, $spec, $error)
            """;
        cmd.Parameters.AddWithValue("$id", s.Id);
        cmd.Parameters.AddWithValue("$mode", s.Mode);
        cmd.Parameters.AddWithValue("$state", s.State);
        cmd.Parameters.AddWithValue("$package", (object?)s.Package ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$serial", (object?)s.DeviceSerial ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$started", (object?)s.StartedUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dur", (object?)s.DurationMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$trace", (object?)s.TraceFile ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$total", (object?)s.TotalSamples ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$withstack", (object?)s.SamplesWithStack ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$spec", (object?)s.SpecJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$error", (object?)s.Error ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public void WriteSampling(SamplingResult r)
    {
        using var tx = _conn.BeginTransaction();
        WriteMethods(r.Methods);
        WriteThreads(r.Threads);
        using (var cmd = Prepare("INSERT INTO sample_stat(method_id, inclusive, exclusive, inclusive_cpu, exclusive_cpu) VALUES ($m,$i,$e,$ic,$ec)", "$m", "$i", "$e", "$ic", "$ec"))
            foreach (var s in r.Stats) Run(cmd, s.MethodId, s.Inclusive, s.Exclusive, s.InclusiveCpu, s.ExclusiveCpu);
        using (var cmd = Prepare("INSERT INTO sample_tree(id, parent_id, method_id, thread_id, depth, inclusive, exclusive, inclusive_cpu, exclusive_cpu) VALUES ($id,$p,$m,$t,$d,$i,$e,$ic,$ec)", "$id", "$p", "$m", "$t", "$d", "$i", "$e", "$ic", "$ec"))
            foreach (var n in r.Tree) Run(cmd, n.Id, (object?)n.ParentId ?? DBNull.Value, n.MethodId, n.ThreadId, n.Depth, n.Inclusive, n.Exclusive, n.InclusiveCpu, n.ExclusiveCpu);
        using (var cmd = Prepare("INSERT INTO sample_edge(caller_method_id, callee_method_id, samples) VALUES ($c,$e,$s)", "$c", "$e", "$s"))
            foreach (var e in r.Edges) Run(cmd, e.CallerMethodId, e.CalleeMethodId, e.Samples);
        tx.Commit();
    }

    public void WriteInstrumenting(InstrumentingResult r)
    {
        using var tx = _conn.BeginTransaction();
        WriteMethods(r.Methods);
        WriteThreads(r.Threads);
        using (var cmd = Prepare("INSERT INTO timing_stat(method_id, calls, total_ns, self_ns, min_ns, max_ns, exception_leaves) VALUES ($m,$c,$t,$s,$mi,$ma,$x)", "$m", "$c", "$t", "$s", "$mi", "$ma", "$x"))
            foreach (var t in r.Timings) Run(cmd, t.MethodId, t.Calls, t.TotalNs, t.SelfNs, t.MinNs, t.MaxNs, t.ExceptionLeaves);
        using (var cmd = Prepare("INSERT INTO timing_tree(id, parent_id, method_id, thread_id, depth, calls, total_ns, self_ns) VALUES ($id,$p,$m,$t,$d,$c,$tot,$s)", "$id", "$p", "$m", "$t", "$d", "$c", "$tot", "$s"))
            foreach (var n in r.Tree) Run(cmd, n.Id, (object?)n.ParentId ?? DBNull.Value, n.MethodId, n.ThreadId, n.Depth, n.Calls, n.TotalNs, n.SelfNs);
        WriteTypes(r.Types);
        using (var cmd = Prepare("INSERT INTO alloc_by_type(type_id, count, bytes) VALUES ($t,$c,$b)", "$t", "$c", "$b"))
            foreach (var a in r.AllocsByType) Run(cmd, a.TypeId, a.Count, a.Bytes);
        using (var cmd = Prepare("INSERT INTO alloc_by_site(type_id, method_id, count, bytes) VALUES ($t,$m,$c,$b)", "$t", "$m", "$c", "$b"))
            foreach (var a in r.AllocsBySite) Run(cmd, a.TypeId, a.MethodId, a.Count, a.Bytes);
        tx.Commit();
    }

    /// <summary>Write a heap snapshot (types are added to the shared type table by name).</summary>
    public int WriteHeapSnapshot(DateTimeOffset takenUtc, string? file, IReadOnlyList<(string typeName, long count, long bytes)> byType)
    {
        using var tx = _conn.BeginTransaction();
        var typeIds = new Dictionary<string, int>();
        using (var q = _conn.CreateCommand())
        {
            q.CommandText = "SELECT id, name FROM type";
            using var rd = q.ExecuteReader();
            while (rd.Read()) typeIds[rd.GetString(1)] = rd.GetInt32(0);
        }
        int next = typeIds.Count == 0 ? 0 : typeIds.Values.Max() + 1;
        using (var ins = Prepare("INSERT INTO type(id, name, vtable_id, class_id) VALUES ($id,$n,0,0)", "$id", "$n"))
        {
            foreach (var (name, _, _) in byType)
                if (!typeIds.ContainsKey(name)) { typeIds[name] = next; Run(ins, next, name); next++; }
        }
        long totalObjects = byType.Sum(t => t.count), totalBytes = byType.Sum(t => t.bytes);
        int snapshotId;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO heap_snapshot(taken_utc, total_objects, total_bytes, file) VALUES ($t,$o,$b,$f); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$t", takenUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$o", totalObjects);
            cmd.Parameters.AddWithValue("$b", totalBytes);
            cmd.Parameters.AddWithValue("$f", (object?)file ?? DBNull.Value);
            snapshotId = Convert.ToInt32(cmd.ExecuteScalar());
        }
        using (var cmd = Prepare("INSERT INTO heap_by_type(snapshot_id, type_id, count, bytes) VALUES ($s,$t,$c,$b)", "$s", "$t", "$c", "$b"))
            foreach (var (name, count, bytes) in byType) Run(cmd, snapshotId, typeIds[name], count, bytes);
        tx.Commit();
        return snapshotId;
    }

    private void WriteMethods(IReadOnlyList<MethodRecord> methods)
    {
        using var cmd = Prepare("INSERT OR REPLACE INTO method(id, module, namespace, type_name, name, signature, full_name, runtime_method_id, is_wait_frame, token) VALUES ($id,$mod,$ns,$t,$n,$sig,$full,$rid,$w,$tok)",
            "$id", "$mod", "$ns", "$t", "$n", "$sig", "$full", "$rid", "$w", "$tok");
        foreach (var m in methods)
            Run(cmd, m.Id, m.Module, m.Namespace, m.TypeName, m.Name, m.Signature, m.FullName, unchecked((long)m.RuntimeMethodId), m.IsWaitFrame ? 1 : 0, m.Token);
    }

    private void WriteThreads(IReadOnlyList<ThreadRecord> threads)
    {
        using var cmd = Prepare("INSERT OR REPLACE INTO thread(id, os_tid, name, samples, first_ms, last_ms) VALUES ($id,$os,$n,$s,$f,$l)", "$id", "$os", "$n", "$s", "$f", "$l");
        foreach (var t in threads)
            Run(cmd, t.Id, t.OsThreadId, (object?)t.Name ?? DBNull.Value, t.Samples, t.FirstMs, t.LastMs);
    }

    private void WriteTypes(IReadOnlyList<TypeRecord> types)
    {
        using var cmd = Prepare("INSERT OR REPLACE INTO type(id, name, vtable_id, class_id) VALUES ($id,$n,$v,$c)", "$id", "$n", "$v", "$c");
        foreach (var t in types)
            Run(cmd, t.Id, t.Name, unchecked((long)t.VTableId), unchecked((long)t.ClassId));
    }

    // ------------------------------------------------------------------- read

    public SessionRow? ReadSession()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, mode, state, package, device_serial, started_utc, duration_ms, trace_file, total_samples, samples_with_stack, spec_json, error FROM session";
        using var rd = cmd.ExecuteReader();
        if (!rd.Read()) return null;
        return new SessionRow(rd.GetString(0), rd.GetString(1), rd.GetString(2), Str(rd, 3), Str(rd, 4),
            Str(rd, 5) is { } s ? DateTimeOffset.Parse(s) : null, rd.IsDBNull(6) ? null : rd.GetDouble(6), Str(rd, 7),
            rd.IsDBNull(8) ? null : rd.GetInt64(8), rd.IsDBNull(9) ? null : rd.GetInt64(9), Str(rd, 10), Str(rd, 11));
    }

    /// <summary>Hottest methods by exclusive (or inclusive) samples; <paramref name="cpuOnly"/> orders by the *_cpu columns and hides pure wait frames.</summary>
    public IReadOnlyList<HotMethodRow> Hotspots(int top, bool exclusive = true, bool cpuOnly = true, string? nameFilter = null)
    {
        string col = (exclusive ? "exclusive" : "inclusive") + (cpuOnly ? "_cpu" : "");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT m.id, m.full_name, m.module, s.inclusive, s.exclusive, s.inclusive_cpu, s.exclusive_cpu, m.is_wait_frame
            FROM sample_stat s JOIN method m ON m.id = s.method_id
            WHERE ($f IS NULL OR m.full_name LIKE $f) AND s.{col} > 0
            ORDER BY s.{col} DESC LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$n", top);
        cmd.Parameters.AddWithValue("$f", (object?)Like(nameFilter) ?? DBNull.Value);
        using var rd = cmd.ExecuteReader();
        var list = new List<HotMethodRow>();
        while (rd.Read())
            list.Add(new HotMethodRow(rd.GetInt32(0), rd.GetString(1), rd.GetString(2), rd.GetInt64(3), rd.GetInt64(4), rd.GetInt64(5), rd.GetInt64(6), rd.GetInt64(7) != 0));
        return list;
    }

    /// <summary>Children of <paramref name="parentId"/> in the sampling tree (null = thread roots), ordered by inclusive desc.</summary>
    public IReadOnlyList<TreeRow> SampleTreeChildren(int? parentId, int top = 50)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT t.id, t.parent_id, t.method_id, COALESCE(m.full_name, '<thread ' || t.thread_id || '>'), t.thread_id, t.depth,
                   t.inclusive, t.exclusive, t.inclusive_cpu, t.exclusive_cpu,
                   EXISTS(SELECT 1 FROM sample_tree c WHERE c.parent_id = t.id)
            FROM sample_tree t LEFT JOIN method m ON m.id = t.method_id
            WHERE ($p IS NULL AND t.parent_id IS NULL) OR t.parent_id = $p
            ORDER BY t.inclusive DESC LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$p", (object?)parentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$n", top);
        using var rd = cmd.ExecuteReader();
        var list = new List<TreeRow>();
        while (rd.Read())
            list.Add(new TreeRow(rd.GetInt32(0), rd.IsDBNull(1) ? null : rd.GetInt32(1), rd.GetInt32(2), rd.GetString(3), rd.GetInt32(4), rd.GetInt32(5),
                0, rd.GetInt64(6), rd.GetInt64(7), rd.GetInt64(8), rd.GetInt64(9), rd.GetInt64(10) != 0));
        return list;
    }

    /// <summary>Children of <paramref name="parentId"/> in the timing tree (null = thread roots), ordered by total ns desc.</summary>
    public IReadOnlyList<TreeRow> TimingTreeChildren(int? parentId, int top = 50)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT t.id, t.parent_id, t.method_id, COALESCE(m.full_name, '<thread ' || t.thread_id || '>'), t.thread_id, t.depth,
                   t.calls, t.total_ns, t.self_ns,
                   EXISTS(SELECT 1 FROM timing_tree c WHERE c.parent_id = t.id)
            FROM timing_tree t LEFT JOIN method m ON m.id = t.method_id
            WHERE ($p IS NULL AND t.parent_id IS NULL) OR t.parent_id = $p
            ORDER BY t.total_ns DESC LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$p", (object?)parentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$n", top);
        using var rd = cmd.ExecuteReader();
        var list = new List<TreeRow>();
        while (rd.Read())
            list.Add(new TreeRow(rd.GetInt32(0), rd.IsDBNull(1) ? null : rd.GetInt32(1), rd.GetInt32(2), rd.GetString(3), rd.GetInt32(4), rd.GetInt32(5),
                rd.GetInt64(6), rd.GetInt64(7), rd.GetInt64(8), 0, 0, rd.GetInt64(9) != 0));
        return list;
    }

    public IReadOnlyList<EdgeRow> Callers(int methodId, int top = 50) => Edges("caller_method_id", "callee_method_id", methodId, top);
    public IReadOnlyList<EdgeRow> Callees(int methodId, int top = 50) => Edges("callee_method_id", "caller_method_id", methodId, top);

    private IReadOnlyList<EdgeRow> Edges(string selectCol, string whereCol, int methodId, int top)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT e.{selectCol}, m.full_name, e.samples FROM sample_edge e JOIN method m ON m.id = e.{selectCol}
            WHERE e.{whereCol} = $m ORDER BY e.samples DESC LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$m", methodId);
        cmd.Parameters.AddWithValue("$n", top);
        using var rd = cmd.ExecuteReader();
        var list = new List<EdgeRow>();
        while (rd.Read()) list.Add(new EdgeRow(rd.GetInt32(0), rd.GetString(1), rd.GetInt64(2)));
        return list;
    }

    public IReadOnlyList<TimingRow> Timings(int top, bool bySelf = false, string? nameFilter = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT m.id, m.full_name, t.calls, t.total_ns, t.self_ns, t.min_ns, t.max_ns, t.exception_leaves
            FROM timing_stat t JOIN method m ON m.id = t.method_id
            WHERE ($f IS NULL OR m.full_name LIKE $f)
            ORDER BY {(bySelf ? "t.self_ns" : "t.total_ns")} DESC LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$n", top);
        cmd.Parameters.AddWithValue("$f", (object?)Like(nameFilter) ?? DBNull.Value);
        using var rd = cmd.ExecuteReader();
        var list = new List<TimingRow>();
        while (rd.Read())
            list.Add(new TimingRow(rd.GetInt32(0), rd.GetString(1), rd.GetInt64(2), rd.GetInt64(3), rd.GetInt64(4), rd.GetInt64(5), rd.GetInt64(6), rd.GetInt64(7)));
        return list;
    }

    public IReadOnlyList<AllocTypeRow> AllocationsByType(int top, bool byCount = false)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT t.id, t.name, a.count, a.bytes FROM alloc_by_type a JOIN type t ON t.id = a.type_id
            ORDER BY {(byCount ? "a.count" : "a.bytes")} DESC LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$n", top);
        using var rd = cmd.ExecuteReader();
        var list = new List<AllocTypeRow>();
        while (rd.Read()) list.Add(new AllocTypeRow(rd.GetInt32(0), rd.GetString(1), rd.GetInt64(2), rd.GetInt64(3)));
        return list;
    }

    public IReadOnlyList<AllocSiteRow> AllocationsBySite(int top, int? typeId = null, int? methodId = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT t.id, t.name, a.method_id, COALESCE(m.full_name, '<no instrumented frame>'), a.count, a.bytes
            FROM alloc_by_site a JOIN type t ON t.id = a.type_id LEFT JOIN method m ON m.id = a.method_id
            WHERE ($t IS NULL OR a.type_id = $t) AND ($m IS NULL OR a.method_id = $m)
            ORDER BY a.bytes DESC LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$n", top);
        cmd.Parameters.AddWithValue("$t", (object?)typeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$m", (object?)methodId ?? DBNull.Value);
        using var rd = cmd.ExecuteReader();
        var list = new List<AllocSiteRow>();
        while (rd.Read()) list.Add(new AllocSiteRow(rd.GetInt32(0), rd.GetString(1), rd.GetInt32(2), rd.GetString(3), rd.GetInt64(4), rd.GetInt64(5)));
        return list;
    }

    public IReadOnlyList<(int snapshotId, string typeName, long count, long bytes)> HeapByType(int snapshotId, int top)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT h.snapshot_id, t.name, h.count, h.bytes FROM heap_by_type h JOIN type t ON t.id = h.type_id WHERE h.snapshot_id = $s ORDER BY h.bytes DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$s", snapshotId);
        cmd.Parameters.AddWithValue("$n", top);
        using var rd = cmd.ExecuteReader();
        var list = new List<(int, string, long, long)>();
        while (rd.Read()) list.Add((rd.GetInt32(0), rd.GetString(1), rd.GetInt64(2), rd.GetInt64(3)));
        return list;
    }

    /// <summary>
    /// Compare two heap snapshots of this session: per type, live objects and bytes
    /// in each snapshot. Types missing from one side count as zero, so both growth
    /// and disappearance show up. Ordered by byte growth (descending) by default.
    /// </summary>
    public IReadOnlyList<HeapDiffRow> HeapDiff(int fromSnapshot, int toSnapshot, int top, bool byCount = false)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT t.name,
                   COALESCE(a.count, 0), COALESCE(b.count, 0),
                   COALESCE(a.bytes, 0), COALESCE(b.bytes, 0)
            FROM type t
            LEFT JOIN heap_by_type a ON a.type_id = t.id AND a.snapshot_id = $from
            LEFT JOIN heap_by_type b ON b.type_id = t.id AND b.snapshot_id = $to
            WHERE a.type_id IS NOT NULL OR b.type_id IS NOT NULL
            ORDER BY ({(byCount ? "COALESCE(b.count,0) - COALESCE(a.count,0)" : "COALESCE(b.bytes,0) - COALESCE(a.bytes,0)")}) DESC
            LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$from", fromSnapshot);
        cmd.Parameters.AddWithValue("$to", toSnapshot);
        cmd.Parameters.AddWithValue("$n", top);
        using var rd = cmd.ExecuteReader();
        var list = new List<HeapDiffRow>();
        while (rd.Read())
            list.Add(new HeapDiffRow(rd.GetString(0), rd.GetInt64(1), rd.GetInt64(2), rd.GetInt64(3), rd.GetInt64(4)));
        return list;
    }

    /// <summary>Snapshots stored in this session (id, taken time, totals).</summary>
    public IReadOnlyList<(int id, DateTimeOffset takenUtc, long objects, long bytes)> HeapSnapshots()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, taken_utc, total_objects, total_bytes FROM heap_snapshot ORDER BY id";
        using var rd = cmd.ExecuteReader();
        var list = new List<(int, DateTimeOffset, long, long)>();
        while (rd.Read()) list.Add((rd.GetInt32(0), DateTimeOffset.Parse(rd.GetString(1)), rd.GetInt64(2), rd.GetInt64(3)));
        return list;
    }

    public IReadOnlyList<ThreadRow> Threads()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, os_tid, name, samples, first_ms, last_ms FROM thread ORDER BY samples DESC";
        using var rd = cmd.ExecuteReader();
        var list = new List<ThreadRow>();
        while (rd.Read())
            list.Add(new ThreadRow(rd.GetInt32(0), rd.GetInt32(1), Str(rd, 2), rd.GetInt64(3), rd.IsDBNull(4) ? null : rd.GetDouble(4), rd.IsDBNull(5) ? null : rd.GetDouble(5)));
        return list;
    }

    /// <summary>Methods of a module with their sampling and/or timing figures, keyed by metadata token (for source annotation).</summary>
    public IReadOnlyList<MethodFigures> MethodFiguresByModule(string module)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT m.id, m.token, m.full_name,
                   COALESCE(s.inclusive, 0), COALESCE(s.exclusive, 0), COALESCE(s.inclusive_cpu, 0), COALESCE(s.exclusive_cpu, 0),
                   COALESCE(t.calls, 0), COALESCE(t.total_ns, 0), COALESCE(t.self_ns, 0)
            FROM method m
            LEFT JOIN sample_stat s ON s.method_id = m.id
            LEFT JOIN timing_stat t ON t.method_id = m.id
            WHERE lower(m.module) = lower($mod) AND m.token <> 0
            """;
        cmd.Parameters.AddWithValue("$mod", module);
        using var rd = cmd.ExecuteReader();
        var list = new List<MethodFigures>();
        while (rd.Read())
            list.Add(new MethodFigures(rd.GetInt32(0), rd.GetInt32(1), rd.GetString(2), rd.GetInt64(3), rd.GetInt64(4), rd.GetInt64(5), rd.GetInt64(6), rd.GetInt64(7), rd.GetInt64(8), rd.GetInt64(9)));
        return list;
    }

    /// <summary>Distinct module names present in the method table.</summary>
    public IReadOnlyList<string> Modules()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT module FROM method WHERE module <> '' ORDER BY module";
        using var rd = cmd.ExecuteReader();
        var list = new List<string>();
        while (rd.Read()) list.Add(rd.GetString(0));
        return list;
    }

    public int? FindMethodId(string fullNameOrSuffix)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM method WHERE full_name = $n OR full_name LIKE $l ORDER BY CASE WHEN full_name = $n THEN 0 ELSE 1 END LIMIT 1";
        cmd.Parameters.AddWithValue("$n", fullNameOrSuffix);
        cmd.Parameters.AddWithValue("$l", "%" + fullNameOrSuffix + "%");
        var r = cmd.ExecuteScalar();
        return r is null or DBNull ? null : Convert.ToInt32(r);
    }

    public long Count(string table)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // ---------------------------------------------------------------- helpers

    private static string? Like(string? filter) => string.IsNullOrWhiteSpace(filter) ? null : "%" + filter.Replace("*", "%") + "%";

    private static string? Str(SqliteDataReader rd, int i) => rd.IsDBNull(i) ? null : rd.GetString(i);

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private SqliteCommand Prepare(string sql, params string[] parameters)
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var p in parameters) cmd.Parameters.Add(new SqliteParameter(p, SqliteType.Integer));
        return cmd;
    }

    private static void Run(SqliteCommand cmd, params object[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            var p = cmd.Parameters[i];
            p.Value = values[i];
            p.ResetSqliteType();
        }
        cmd.ExecuteNonQuery();
    }
}
