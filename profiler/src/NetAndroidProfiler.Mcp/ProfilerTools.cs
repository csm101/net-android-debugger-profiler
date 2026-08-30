using System.ComponentModel;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using NetAndroidProfiler.Core.Apps;
using NetAndroidProfiler.Core.Devices;
using NetAndroidProfiler.Core.Projects;
using NetAndroidProfiler.Core.Sessions;

namespace NetAndroidProfiler.Mcp;

/// <summary>
/// MCP tool surface: a thin translation over <see cref="ProfilerSession"/> and
/// its result store. Every tool returns compact plain text for the model.
/// </summary>
[McpServerToolType]
public sealed class ProfilerTools(SessionHost host)
{
    // ------------------------------------------------------------------ devices

    [McpServerTool(Name = "list_devices", ReadOnly = true), Description("Lists adb devices/emulators (serial, model, API level, ABI). Pick a serial for profile_run.")]
    public async Task<string> ListDevices(CancellationToken ct)
    {
        var devices = await new AdbClient().ListDevicesAsync(ct);
        if (devices.Count == 0) return "No adb devices attached.";
        var sb = new StringBuilder();
        foreach (var d in devices)
            sb.AppendLine($"{d.Serial}  state={d.State}  model={d.Model ?? "?"}  api={d.ApiLevel}  abi={d.Abi}{(d.IsEmulator ? $"  (emulator{(d.AvdName is null ? "" : " " + d.AvdName)})" : "")}");
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "check_app", ReadOnly = true), Description(
        "Inspects the installed APK of a package and reports whether it can be profiled in each mode " +
        "(diagnostics component, AOT, debuggable, baked MONO_DIAGNOSTICS) with guidance for what is missing.")]
    public async Task<string> CheckApp(
        [Description("adb serial (from list_devices)")] string deviceSerial,
        [Description("Android package name (ApplicationId)")] string packageName,
        CancellationToken ct = default)
    {
        var adb = new AdbClient();
        var dev = (await adb.ListDevicesAsync(ct)).FirstOrDefault(d => d.Serial == deviceSerial)
            ?? throw new McpException($"Device {deviceSerial} not attached.");
        var p = await new AppInspector(adb).InspectAsync(deviceSerial, packageName, dev.Abi, ct);
        var sb = new StringBuilder();
        sb.AppendLine($"package={p.Package} abi={p.Abi} debuggable={p.IsDebuggable} diagnosticsComponent={p.HasDiagnosticsComponent} aot={p.HasAotLibraries} monoDiagnosticsBaked={p.HasMonoDiagnosticsBaked}");
        foreach (var mode in Enum.GetValues<ProfilingMode>())
        {
            var problems = p.Check(mode);
            sb.AppendLine($"{mode}: {(problems.Any(x => x.IsBlocking) ? "NOT AVAILABLE" : problems.Count > 0 ? "ok with warnings" : "ok")}");
            foreach (var pr in problems) sb.AppendLine($"  - {(pr.IsBlocking ? "blocking" : "warning")}: {pr.Message}");
        }
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "list_app_projects", ReadOnly = true), Description(
        "Reads a solution, a .csproj or a folder of sources and lists the .NET for Android application projects in it, " +
        "with the package they install, the build output holding their pdbs (pass it as symbolsDir), and the assemblies " +
        "to weave. Use it to turn 'here are the sources' into the arguments profile_run needs.")]
    public string ListAppProjects(
        [Description("Solution (.sln/.slnx), project (.csproj) or folder to search")] string path,
        [Description("Build configuration whose output directory is reported")] string configuration = "Debug")
    {
        var projects = AppProjectFinder.Find(path, configuration);
        if (projects.Count == 0) return $"No .NET for Android application project under {path}.";
        var sb = new StringBuilder();
        foreach (var p in projects)
        {
            sb.AppendLine($"{p.Name}  package={p.ApplicationId ?? "(not declared)"}  tfm={p.TargetFramework}");
            sb.AppendLine($"  project      {p.ProjectPath}");
            sb.AppendLine($"  symbolsDir   {p.OutputDir}{(p.OutputExists ? "" : "   (not built yet)")}");
            sb.AppendLine($"  assemblies   {string.Join(", ", p.Assemblies)}");
            if (p.EnableDiagnostics != true)
                sb.AppendLine("  warning      EnableDiagnostics is not in the project file: build with -p:EnableDiagnostics=true (docs/APP_SETUP.md).");
            if (p.EmbedAssembliesIntoApk == true)
                sb.AppendLine("  warning      EmbedAssembliesIntoApk=true: the weaver engines need fast deployment, or a build-time weave.");
        }
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ sessions

    [McpServerTool(Name = "profile_run"), Description(
        "One-shot profiling session: configures the device/app, collects for durationSeconds, analyzes into a SQLite database and returns a summary. " +
        "mode: sampling (CPU, default), instrumenting (exact enter/leave timings + allocations, needs callspec, restarts the app), heap (live-heap snapshot by type). " +
        "launch: restart (default; suspend until the session is up) or attach (app already running; sampling/heap only).")]
    public async Task<string> ProfileRun(
        [Description("adb serial of the device")] string deviceSerial,
        [Description("Android package name (ApplicationId)")] string packageName,
        [Description("sampling | instrumenting | heap")] string mode = "sampling",
        [Description("Collection time in seconds (ignored for heap)")] int durationSeconds = 20,
        [Description("restart | attach")] string launch = "restart",
        [Description("Instrumenting: Mono callspec, e.g. 'N:My.App' or 'T:My.App.Service,M:My.App.Other:Method'. Exclude hot leaf methods.")] string? callspec = null,
        [Description("Instrumenting: also record every allocation (type, size, allocating method)")] bool trackAllocations = true,
        [Description("restart: keep the app suspended until the session is up (captures startup)")] bool suspendOnStart = true,
        [Description("Optional friendly name used in the session id")] string? name = null,
        [Description("restart: leave the app running after the session (default: stop it, so it does not reconnect to the next session)")] bool keepAppRunning = false,
        [Description("Instrumenting engine: auto (default: weaver-tree when the app's assemblies can be rewritten, provider when they cannot), weaver-tree (IL weaving, the app keeps a call tree: cheapest, no per-call events), weaver (IL weaving with an event per call: keeps their order and every single duration), provider (Mono runtime callspec; crashes net9 runtimes)")] string engine = "auto",
        [Description("Weaver: assembly names to weave, comma-separated (e.g. 'App.Droid,App.Core'); inferred from the callspec when omitted")] string? weaveAssemblies = null,
        [Description("Weaver: local directories with the app's reference assemblies (usually its bin/<Config>/<tfm> folder); needed because most assemblies live in the APK assembly store, not on the device")] string? weaveReferenceDirs = null,
        [Description("Weaver: path of nap-weave.map from a build-time weaving build (-p:NapWeave=true). With it nothing is woven or deployed on the device: the installed app already carries the instrumentation.")] string? weaveMapPath = null,
        [Description("heap mode: how many snapshots to take (2 enables heap_diff, i.e. leak hunting)")] int snapshots = 1,
        [Description("heap mode: seconds between snapshots")] int snapshotIntervalSeconds = 30,
        [Description("Weaver: also instrument property getters/setters (skipped by default: they are trivial and called everywhere)")] bool weavePropertyAccessors = false,
        [Description("Weaver: instrument async state machines too, reported as '<method> (async body)' - their calls are resumptions and their time excludes the awaits. On by default; turn it off to weave only the synchronous stub of async methods")] bool weaveAsyncBodies = true,
        [Description("Stop collecting when the trace reaches this many MB (default 512; 0 = no limit). A real app samples at roughly 1.5 MB/s and instrumenting traces grow faster")] int maxTraceMb = 512,
        [Description("Build output with the app's portable .pdb files (bin/<Configuration>/<tfm>). Given this, the results record where each method lives, which is what lets a GUI show the source beside the figures")] string? symbolsDir = null,
        CancellationToken ct = default)
    {
        var spec = BuildSpec(deviceSerial, packageName, mode, durationSeconds, launch, callspec, trackAllocations, suspendOnStart, name, keepAppRunning, engine, weaveAssemblies, weaveReferenceDirs, weaveMapPath, snapshots, snapshotIntervalSeconds, weavePropertyAccessors, weaveAsyncBodies, maxTraceMb, symbolsDir);
        var live = host.Create(spec);
        SessionInfo info;
        try { info = await live.Session.RunAsync(ct); }
        catch (Exception e) when (e is ProfilerException or ToolException)
        {
            throw new McpException(e.Message + "\n" + string.Join("\n", live.Session.LogLines.TakeLast(15)));
        }
        return TextFormat.Info(info) + "\n\n" + Summary(info.Id);
    }

    [McpServerTool(Name = "profile_start"), Description(
        "Starts a profiling session that runs until profile_stop (no fixed duration). Returns immediately with the session id; use profile_status to follow it.")]
    public string ProfileStart(
        [Description("adb serial of the device")] string deviceSerial,
        [Description("Android package name (ApplicationId)")] string packageName,
        [Description("sampling | instrumenting")] string mode = "sampling",
        [Description("restart | attach")] string launch = "restart",
        [Description("Instrumenting: Mono callspec")] string? callspec = null,
        [Description("Instrumenting: also record allocations")] bool trackAllocations = true,
        [Description("restart: suspend the app until the session is up")] bool suspendOnStart = true,
        [Description("Optional friendly name")] string? name = null,
        [Description("restart: leave the app running after the session")] bool keepAppRunning = false,
        [Description("Instrumenting engine: auto (default: weaver-tree when the app's assemblies can be rewritten, provider when they cannot), weaver-tree (IL weaving, the app keeps a call tree: cheapest, no per-call events), weaver (IL weaving with an event per call: keeps their order and every single duration), provider (Mono runtime callspec; crashes net9 runtimes)")] string engine = "auto",
        [Description("Weaver: assembly names to weave, comma-separated")] string? weaveAssemblies = null,
        [Description("Weaver: local reference directories (app bin folder)")] string? weaveReferenceDirs = null,
        [Description("Weaver: path of nap-weave.map from a build-time weaving build")] string? weaveMapPath = null,
        [Description("Stop collecting when the trace reaches this many MB (default 512; 0 = no limit). Sessions without a duration are exactly the ones that can run away")] int maxTraceMb = 512,
        [Description("Build output with the app's portable .pdb files, so the results record where each method lives")] string? symbolsDir = null)
    {
        var spec = BuildSpec(deviceSerial, packageName, mode, null, launch, callspec, trackAllocations, suspendOnStart, name, keepAppRunning, engine, weaveAssemblies, weaveReferenceDirs, weaveMapPath, maxTraceMb: maxTraceMb, symbolsDir: symbolsDir);
        if (spec.Mode == ProfilingMode.HeapSnapshot) throw new McpException("heap snapshots are one-shot: use profile_run with mode=heap.");
        var live = host.Create(spec);
        live.RunTask = Task.Run(() => live.Session.RunAsync(CancellationToken.None));
        return $"Started session {live.Session.Id}. Call profile_status to watch it, profile_stop to end collection and analyze.";
    }

    [McpServerTool(Name = "profile_stop"), Description("Ends collection of a running session (default: the current one), waits for the analysis and returns the summary.")]
    public async Task<string> ProfileStop([Description("Session id (default: current)")] string? sessionId = null, CancellationToken ct = default)
    {
        var live = host.Live(sessionId) ?? throw new McpException("No running session in this server. See profile_sessions for finished ones.");
        if (live.RunTask is null) throw new McpException($"Session {live.Session.Id} was not started with profile_start.");
        live.Session.Stop();
        SessionInfo info;
        try { info = await live.RunTask.WaitAsync(ct); }
        catch (Exception e) when (e is ProfilerException or ToolException) { throw new McpException(e.Message + "\n" + string.Join("\n", live.Session.LogLines.TakeLast(15))); }
        return TextFormat.Info(info) + "\n\n" + Summary(info.Id);
    }

    [McpServerTool(Name = "profile_snapshot"), Description(
        "Refreshes the results of a running session from what has been collected so far, without stopping the app " +
        "(AQTime's Get Results). Weaver sessions only: a runtime-provider trace only resolves method names when its " +
        "session ends. After this, the read-only tools show the profile up to now.")]
    public async Task<string> ProfileSnapshot([Description("Session id (default: current)")] string? sessionId = null, CancellationToken ct = default)
    {
        var live = LiveOrThrow(sessionId);
        try
        {
            int segment = await live.Session.SnapshotAsync(ct);
            return $"Snapshot {segment} written to {live.Session.DatabasePath}. The session keeps collecting." + Environment.NewLine + Summary(live.Session.Id);
        }
        catch (Exception e) when (e is ProfilerException or ToolException) { throw new McpException(e.Message); }
    }

    [McpServerTool(Name = "profile_pause"), Description(
        "Stops recording events without stopping the app (AQTime's Disable Profiling). Weaver sessions only. " +
        "The methods stay woven, so their overhead remains while paused.")]
    public async Task<string> ProfilePause([Description("Session id (default: current)")] string? sessionId = null, CancellationToken ct = default)
    {
        var live = LiveOrThrow(sessionId);
        try { await live.Session.PauseAsync(ct); return $"Session {live.Session.Id} paused (takes effect within a second)."; }
        catch (Exception e) when (e is ProfilerException or ToolException) { throw new McpException(e.Message); }
    }

    [McpServerTool(Name = "profile_resume"), Description("Resumes recording after profile_pause. Weaver sessions only.")]
    public async Task<string> ProfileResume([Description("Session id (default: current)")] string? sessionId = null, CancellationToken ct = default)
    {
        var live = LiveOrThrow(sessionId);
        try { await live.Session.ResumeAsync(ct); return $"Session {live.Session.Id} resumed."; }
        catch (Exception e) when (e is ProfilerException or ToolException) { throw new McpException(e.Message); }
    }

    [McpServerTool(Name = "profile_clear"), Description(
        "Throws away what a running session has collected so far and keeps going (AQTime's Clear Results). " +
        "Weaver sessions only. Clearing does not remove instrumentation.")]
    public async Task<string> ProfileClear([Description("Session id (default: current)")] string? sessionId = null, CancellationToken ct = default)
    {
        var live = LiveOrThrow(sessionId);
        try { await live.Session.ClearAsync(ct); return $"Session {live.Session.Id}: results cleared, still collecting."; }
        catch (Exception e) when (e is ProfilerException or ToolException) { throw new McpException(e.Message); }
    }

    private SessionRegistry.LiveSession LiveOrThrow(string? sessionId) =>
        host.Live(sessionId) ?? throw new McpException("No running session in this server. See profile_sessions for finished ones.");

    [McpServerTool(Name = "profile_archive"), Description(
        "Keeps the results as they are now, under a name, and goes on profiling: the AQTime habit of collecting a " +
        "Get Results and never losing it. On a running weaver session this refreshes the results first. An archive is " +
        "an ordinary result database: profile_archives lists them and every read-only tool opens one through sessionId.")]
    public async Task<string> ProfileArchive(
        [Description("What to call it; a dated default is used when omitted")] string? name = null,
        [Description("Session id (default: current)")] string? sessionId = null,
        CancellationToken ct = default)
    {
        var live = LiveOrThrow(sessionId);
        try
        {
            var archive = await live.Session.ArchiveAsync(name, ct);
            return $"Archived '{archive.Name}' ({archive.CreatedUtc.ToLocalTime():HH:mm:ss}). The session keeps collecting."
                + Environment.NewLine + archive.Path;
        }
        catch (Exception e) when (e is ProfilerException or ToolException) { throw new McpException(e.Message); }
    }

    [McpServerTool(Name = "profile_archives", ReadOnly = true), Description(
        "The archived results of a session, newest first. They outlive the session: open one by passing its path as sessionId.")]
    public string ProfileArchives([Description("Session id (default: current/last)")] string? sessionId = null)
    {
        var archives = ProfilerSession.ListArchives(Path.Combine(host.SessionsRoot, host.ResolveId(sessionId)));
        if (archives.Count == 0) return "No archived results for this session.";
        var sb = new StringBuilder();
        foreach (var a in archives)
        {
            sb.AppendLine($"{a.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {a.Name}");
            sb.AppendLine("  " + a.Path);
        }
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "profile_status", ReadOnly = true), Description("State of a session started in this server (default: current) with the last log lines.")]
    public string ProfileStatus([Description("Session id (default: current)")] string? sessionId = null, [Description("Log lines to include")] int logLines = 15)
    {
        var live = host.Live(sessionId);
        if (live is null) return "No live session in this server process. profile_sessions lists finished sessions on disk.";
        var info = live.Session.Info;
        return TextFormat.Info(info) + "\n" + string.Join("\n", live.Session.LogLines.TakeLast(Math.Max(0, logLines)));
    }

    [McpServerTool(Name = "profile_sessions", ReadOnly = true), Description("Lists the sessions stored under the sessions root (newest first).")]
    public string ProfileSessions([Description("Max entries")] int max = 20)
    {
        var list = ProfilerSession.ListSessions(host.SessionsRoot).Take(Math.Max(1, max)).ToList();
        if (list.Count == 0) return $"No sessions under {host.SessionsRoot}.";
        var sb = new StringBuilder($"sessions root: {host.SessionsRoot}\n");
        foreach (var s in list)
            sb.AppendLine($"{s.id}  {(s.ready ? "ready" : "incomplete")}  mode={s.spec?.Mode}  package={s.spec?.Package}  device={s.spec?.DeviceSerial}");
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ results

    [McpServerTool(Name = "profile_hotspots", ReadOnly = true), Description(
        "Hottest methods of a sampling session. Counts are samples (~1 ms); inclusive = method on stack, exclusive = method on top; *_cpu exclude samples where the thread was blocked (Sleep/Wait).")]
    public string ProfileHotspots(
        [Description("Session id (default: current/last)")] string? sessionId = null,
        [Description("Rows")] int top = 30,
        [Description("Order by exclusive (true) or inclusive (false)")] bool exclusive = true,
        [Description("Order by CPU-only counts and hide wait frames")] bool cpuOnly = true,
        [Description("Substring/wildcard filter on the full method name, e.g. 'MyApp.*'")] string? filter = null)
    {
        using var s = host.OpenResults(sessionId, out _);
        var session = s.ReadSession();
        return TextFormat.Hotspots(s.Hotspots(top, exclusive, cpuOnly, filter), session?.SamplesWithStack, exclusive, cpuOnly);
    }

    [McpServerTool(Name = "profile_flat", ReadOnly = true), Description("Flat profile: every method with samples, ordered by inclusive CPU samples (alias of profile_hotspots with exclusive=false).")]
    public string ProfileFlat([Description("Session id")] string? sessionId = null, [Description("Rows")] int top = 50, [Description("Name filter")] string? filter = null)
        => ProfileHotspots(sessionId, top, exclusive: false, cpuOnly: true, filter);

    [McpServerTool(Name = "profile_tree", ReadOnly = true), Description(
        "Aggregated call tree. Without nodeId returns the thread roots; pass a node id to list its children. Sampling sessions show sample counts, instrumenting sessions show calls and total/self milliseconds.")]
    public string ProfileTree([Description("Session id")] string? sessionId = null, [Description("Node id to expand (omit for roots)")] int? nodeId = null, [Description("Max children")] int top = 30)
    {
        using var s = host.OpenResults(sessionId, out _);
        bool timing = s.ReadSession()?.Mode == ProfilingMode.Instrumenting.ToString();
        var rows = timing ? s.TimingTreeChildren(nodeId, top) : s.SampleTreeChildren(nodeId, top);
        return TextFormat.Tree(rows, timing);
    }

    [McpServerTool(Name = "profile_callers", ReadOnly = true), Description("Methods that call the given method (sampling sessions), with the number of samples of the edge.")]
    public string ProfileCallers([Description("Method id or (part of) its full name")] string method, [Description("Session id")] string? sessionId = null, [Description("Rows")] int top = 30)
    {
        using var s = host.OpenResults(sessionId, out _);
        int id = ResolveMethod(s, method);
        return TextFormat.Edges($"callers of #{id}", s.Callers(id, top));
    }

    [McpServerTool(Name = "profile_callees", ReadOnly = true), Description("Methods called by the given method (sampling sessions), with the number of samples of the edge.")]
    public string ProfileCallees([Description("Method id or (part of) its full name")] string method, [Description("Session id")] string? sessionId = null, [Description("Rows")] int top = 30)
    {
        using var s = host.OpenResults(sessionId, out _);
        int id = ResolveMethod(s, method);
        return TextFormat.Edges($"callees of #{id}", s.Callees(id, top));
    }

    [McpServerTool(Name = "profile_timings", ReadOnly = true), Description("Instrumenting sessions: per-method call count, total/self time, min/max/avg, exception exits.")]
    public string ProfileTimings([Description("Session id")] string? sessionId = null, [Description("Rows")] int top = 30, [Description("Order by self time instead of total")] bool bySelf = false, [Description("Name filter")] string? filter = null)
    {
        using var s = host.OpenResults(sessionId, out _);
        return TextFormat.Timings(s.Timings(top, bySelf, filter));
    }

    [McpServerTool(Name = "alloc_report", ReadOnly = true), Description("Instrumenting sessions with trackAllocations: allocations per type (count, bytes) and, with bySite=true, per allocating instrumented method.")]
    public string AllocReport([Description("Session id")] string? sessionId = null, [Description("Rows")] int top = 30, [Description("Group by (type, allocating method) instead of type")] bool bySite = false, [Description("Order by count instead of bytes")] bool byCount = false)
    {
        using var s = host.OpenResults(sessionId, out _);
        return bySite ? TextFormat.AllocSites(s.AllocationsBySite(top)) : TextFormat.AllocTypes(s.AllocationsByType(top, byCount));
    }

    [McpServerTool(Name = "heap_report", ReadOnly = true), Description("Heap sessions: live objects per type (count, bytes) of a snapshot.")]
    public string HeapReport([Description("Session id")] string? sessionId = null, [Description("Snapshot id (default 1)")] int snapshot = 1, [Description("Rows")] int top = 40)
    {
        using var s = host.OpenResults(sessionId, out _);
        return TextFormat.Heap(s.HeapByType(snapshot, top));
    }

    [McpServerTool(Name = "heap_diff", ReadOnly = true), Description(
        "Compares two heap snapshots of a memory session (profile_run with mode=heap and snapshots=2): live objects and bytes " +
        "per type in each snapshot with the growth, ordered by bytes gained. The types at the top are the leak candidates.")]
    public string HeapDiff(
        [Description("Session id (default: current/last)")] string? sessionId = null,
        [Description("First snapshot id")] int from = 1,
        [Description("Second snapshot id")] int to = 2,
        [Description("Rows")] int top = 30,
        [Description("Order by object growth instead of bytes")] bool byCount = false)
    {
        using var s = host.OpenResults(sessionId, out _);
        var snapshots = s.HeapSnapshots();
        if (snapshots.Count < 2)
            throw new McpException($"The session has {snapshots.Count} heap snapshot(s); a diff needs two. Run profile_run with mode=heap and snapshots=2.");
        return TextFormat.HeapDiff(snapshots, s.HeapDiff(from, to, top, byCount));
    }

    [McpServerTool(Name = "profile_threads", ReadOnly = true), Description("Threads seen in the session with their sample counts.")]
    public string ProfileThreads([Description("Session id")] string? sessionId = null)
    {
        using var s = host.OpenResults(sessionId, out _);
        return TextFormat.Threads(s.Threads());
    }

    [McpServerTool(Name = "profile_report", ReadOnly = true), Description("Summary of a session: what it was, top hotspots / timings / allocations depending on the mode, and where the SQLite database is.")]
    public string ProfileReport([Description("Session id (default: current/last)")] string? sessionId = null) => Summary(host.ResolveId(sessionId));

    // ------------------------------------------------------------------ source annotation

    [McpServerTool(Name = "profile_annotate_source", ReadOnly = true), Description(
        "Shows a source file with the session's figures beside each method (MonoVM gives no per-line samples: figures are per method, on the method's first line, " +
        "with the range marked). Needs the portable .pdb files of the build (symbolsDir = the app's bin/<Configuration>/<tfm>/ folder). " +
        "sourceFile is matched as a path suffix against the pdb documents (e.g. 'Workloads/CpuBurner.cs').")]
    public string AnnotateSource(
        [Description("Directory containing the app's *.pdb files (build output)")] string symbolsDir,
        [Description("Source file path or suffix as recorded in the pdb, e.g. 'Services/Sync.cs'")] string sourceFile,
        [Description("Session id (default: current/last)")] string? sessionId = null,
        [Description("Only print methods with figures plus this many context lines (0 = whole file)")] int context = 0)
    {
        try { return AnnotateSourceCore(symbolsDir, sourceFile, sessionId, context); }
        catch (McpException) { throw; }
        catch (Exception e) { throw new McpException($"profile_annotate_source failed: {e.Message}"); }
    }

    private string AnnotateSourceCore(string symbolsDir, string sourceFile, string? sessionId, int context)
    {
        using var s = host.OpenResults(sessionId, out _);
        using var pdbs = Core.Symbols.PortablePdbSymbols.LoadDirectory(symbolsDir);
        if (pdbs.Modules.Count == 0) throw new McpException($"No portable pdb files in {symbolsDir} (DebugType must be portable).");
        var methods = pdbs.MethodsInDocument(sourceFile);
        if (methods.Count == 0)
        {
            var docs = pdbs.Documents().Where(d => d.Contains(Path.GetFileNameWithoutExtension(sourceFile), StringComparison.OrdinalIgnoreCase)).Take(10).ToList();
            throw new McpException($"No methods found for '{sourceFile}' in the pdbs of {symbolsDir}." + (docs.Count > 0 ? " Similar documents: " + string.Join("; ", docs) : ""));
        }
        string doc = methods[0].Document;
        string[] lines = File.Exists(doc) ? File.ReadAllLines(doc) : (File.Exists(sourceFile) ? File.ReadAllLines(sourceFile) : []);
        bool timing = s.ReadSession()?.Mode == ProfilingMode.Instrumenting.ToString();
        var figures = new Dictionary<(string, int), Core.Store.MethodFigures>();
        foreach (var mod in methods.Select(m => m.Module).Distinct())
            foreach (var f in s.MethodFiguresByModule(mod)) figures[(mod.ToLowerInvariant(), f.Token)] = f;

        var byLine = new Dictionary<int, List<(Core.Symbols.MethodSourceRange range, Core.Store.MethodFigures? fig)>>();
        foreach (var m in methods)
        {
            figures.TryGetValue((m.Module.ToLowerInvariant(), m.Token), out var fig);
            if (!byLine.TryGetValue(m.StartLine, out var l)) byLine[m.StartLine] = l = new();
            l.Add((m, fig));
        }
        var sb = new StringBuilder();
        sb.AppendLine($"{doc}  ({(timing ? "calls / total_ms / self_ms" : "incl / excl / excl_cpu samples")} per method; '|' marks the method's line range)");
        var inRange = new HashSet<int>();
        foreach (var m in methods) if (figures.ContainsKey((m.Module.ToLowerInvariant(), m.Token))) for (int ln = m.StartLine; ln <= m.EndLine; ln++) inRange.Add(ln);
        var show = new HashSet<int>();
        if (context > 0)
            foreach (var m in methods.Where(m => figures.ContainsKey((m.Module.ToLowerInvariant(), m.Token))))
                for (int ln = Math.Max(1, m.StartLine - context); ln <= Math.Min(lines.Length, m.EndLine + context); ln++) show.Add(ln);
        if (lines.Length == 0)
        {
            sb.AppendLine("(source text not found on this machine; listing methods only)");
            foreach (var m in methods)
            {
                figures.TryGetValue((m.Module.ToLowerInvariant(), m.Token), out var fig);
                sb.AppendLine($"{m.StartLine,5}-{m.EndLine,-5} {Fig(fig, timing),-28} {fig?.FullName ?? $"token 0x{m.Token:X8}"}");
            }
            return sb.ToString().TrimEnd();
        }
        int last = 0;
        for (int i = 1; i <= lines.Length; i++)
        {
            if (context > 0 && !show.Contains(i)) continue;
            if (context > 0 && last != 0 && i != last + 1) sb.AppendLine("   ...");
            last = i;
            string fig = "";
            if (byLine.TryGetValue(i, out var ms))
                fig = string.Join(" ", ms.Select(x => Fig(x.fig, timing)));
            sb.AppendLine($"{i,5} {(inRange.Contains(i) ? "|" : " ")} {fig,-28} {lines[i - 1]}");
        }
        return sb.ToString().TrimEnd();

        static string Fig(Core.Store.MethodFigures? f, bool timing)
        {
            if (f is null) return "";
            return timing
                ? $"[{f.Calls} {f.TotalNs / 1e6:F2} {f.SelfNs / 1e6:F2}]"
                : $"[{f.Inclusive} {f.Exclusive} {f.ExclusiveCpu}]";
        }
    }

    // ------------------------------------------------------------------ app output

    [McpServerTool(Name = "get_app_output", ReadOnly = true), Description("Current logcat lines of the app process (by package), most recent last.")]
    public async Task<string> GetAppOutput([Description("adb serial")] string deviceSerial, [Description("Package name")] string packageName, [Description("Max lines")] int lines = 100, CancellationToken ct = default)
    {
        var adb = new AdbClient();
        var pid = await adb.PidOfAsync(deviceSerial, packageName, ct);
        if (pid is null) return $"{packageName} is not running on {deviceSerial}.";
        var text = await adb.LogcatDumpAsync(deviceSerial, pid, ct);
        var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join("\n", all.TakeLast(Math.Max(1, lines)));
    }

    // ------------------------------------------------------------------ helpers

    private string Summary(string id)
    {
        using var s = host.OpenResults(id, out _);
        var row = s.ReadSession();
        var sb = new StringBuilder();
        sb.AppendLine($"session={id} mode={row?.Mode} state={row?.State} package={row?.Package} device={row?.DeviceSerial} duration={row?.DurationMs / 1000.0:F1}s");
        sb.AppendLine($"database={s.DataSource}");
        if (row?.Mode == ProfilingMode.Sampling.ToString())
        {
            sb.AppendLine($"samples={row.TotalSamples} withStack={row.SamplesWithStack} threads={s.Threads().Count}");
            sb.AppendLine("\nTop exclusive CPU:");
            sb.AppendLine(TextFormat.Hotspots(s.Hotspots(10, true, true), row.SamplesWithStack, true, true));
            sb.AppendLine("\nTop inclusive CPU:");
            sb.AppendLine(TextFormat.Hotspots(s.Hotspots(10, false, true), row.SamplesWithStack, false, true));
        }
        else if (row?.Mode == ProfilingMode.Instrumenting.ToString())
        {
            sb.AppendLine("\nTop methods by total time:");
            sb.AppendLine(TextFormat.Timings(s.Timings(10)));
            sb.AppendLine("\nTop allocations by bytes:");
            sb.AppendLine(TextFormat.AllocTypes(s.AllocationsByType(10)));
        }
        else if (row?.Mode == ProfilingMode.HeapSnapshot.ToString())
        {
            sb.AppendLine("\nLive heap by type:");
            sb.AppendLine(TextFormat.Heap(s.HeapByType(1, 15)));
        }
        return sb.ToString().TrimEnd();
    }

    private static int ResolveMethod(Core.Store.ResultStore s, string method)
    {
        if (int.TryParse(method, out int id)) return id;
        return s.FindMethodId(method) ?? throw new McpException($"No method matches '{method}'.");
    }

    /// <summary>Translate the tool arguments into a spec; Core owns the aliases and the validation.</summary>
    private static SessionSpec BuildSpec(string deviceSerial, string packageName, string mode, int? durationSeconds, string launch, string? callspec, bool trackAllocations, bool suspendOnStart, string? name, bool keepAppRunning, string engine = "auto", string? weaveAssemblies = null, string? weaveReferenceDirs = null, string? weaveMapPath = null, int snapshots = 1, int snapshotIntervalSeconds = 30, bool weavePropertyAccessors = false, bool weaveAsyncBodies = true, int maxTraceMb = 512, string? symbolsDir = null)
    {
        try
        {
            return SessionSpecFactory.Build(deviceSerial, packageName, mode, launch, durationSeconds, callspec,
                trackAllocations, suspendOnStart, name, keepAppRunning, engine,
                SessionSpecFactory.SplitList(weaveAssemblies), SessionSpecFactory.SplitList(weaveReferenceDirs),
                weaveMapPath, snapshots, snapshotIntervalSeconds, weavePropertyAccessors, weaveAsyncBodies, maxTraceMb, symbolsDir);
        }
        catch (ProfilerException e)
        {
            throw new McpException(e.Message);
        }
    }
}
