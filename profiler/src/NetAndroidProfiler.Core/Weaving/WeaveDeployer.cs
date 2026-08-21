using NetAndroidProfiler.Core.Devices;

namespace NetAndroidProfiler.Core.Weaving;

/// <summary>
/// On-device weaving flow for a debuggable (fast-deployment) app: the target
/// assemblies live as writable files under
/// <c>files/.__override__/&lt;abi&gt;/</c>. This pulls the selected ones, weaves
/// them locally, pushes the woven copies back (keeping backups) together with
/// the collector assembly, and restores the originals afterwards. Requires no
/// rebuild of the app.
/// </summary>
public sealed class WeaveDeployer
{
    private readonly AdbClient _adb;
    private readonly string _serial;
    private readonly string _package;
    private readonly string _abi;
    private readonly string _workDir;
    private readonly List<string> _deployed = new();
    private readonly List<string> _movedPdbs = new();
    private bool _collectorDeployed;

    public WeaveDeployer(AdbClient adb, string serial, string package, string abi, string workDir)
    {
        _adb = adb; _serial = serial; _package = package; _abi = abi; _workDir = workDir;
    }

    private string OverrideDir => $"files/.__override__/{_abi}";

    /// <summary>Path of the collector assembly in Core's output (copied there at build time).</summary>
    public static string DefaultCollectorPath =>
        Path.Combine(AppContext.BaseDirectory, CecilWeaver.CollectorAssemblyName + ".dll");

    /// <summary>
    /// Weave <paramref name="assemblies"/> (simple names, e.g. "App.Droid") with
    /// <paramref name="filter"/> and deploy them plus the collector. Returns the
    /// weaver id map. The app must be stopped.
    /// </summary>
    public async Task<IReadOnlyList<WovenMethod>> WeaveAndDeployAsync(IReadOnlyList<string> assemblies, WeaveFilter filter, string? collectorPath, CancellationToken ct, IReadOnlyList<string>? referenceSearchDirs = null, bool weavePropertyAccessors = false, bool trackAllocations = false, bool weaveAsyncBodies = true)
    {
        string pulled = Path.Combine(_workDir, "pulled");
        string wovenDir = Path.Combine(_workDir, "woven");
        Directory.CreateDirectory(pulled);
        Directory.CreateDirectory(wovenDir);

        var weaver = new CecilWeaver(filter, 1, weavePropertyAccessors, trackAllocations, weaveAsyncBodies);
        // Resolve references (constants' types etc.) by pulling siblings from the
        // override dir on demand instead of pulling all ~150 deployed assemblies.
        var resolver = new DeviceAssemblyResolver(pulled, (dllName, localPath) =>
            PullFromOverrideSync(dllName, localPath, ct));
        // Most app assemblies live inside the APK's assembly store, not as files on the
        // device, so references are resolved from local directories (typically the app's
        // build output) when provided.
        foreach (var dir in referenceSearchDirs ?? [])
            if (Directory.Exists(dir)) resolver.AddSearchDirectory(dir);
        var deployedNow = new List<(string remote, string localWoven)>();
        foreach (var name in assemblies)
        {
            ct.ThrowIfCancellationRequested();
            string dll = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll";
            string remote = $"{OverrideDir}/{dll}";
            // A previous session may have left a woven copy in place (restore failed):
            // put the pristine assembly back before weaving, never weave a woven file.
            if (await RemoteExistsAsync(remote + ".naporig", ct).ConfigureAwait(false))
            {
                await RestoreOneAsync(remote, ct).ConfigureAwait(false);
            }
            if (!await RemoteExistsAsync(remote, ct).ConfigureAwait(false))
                throw new ToolException($"Assembly {dll} is not in the app override directory ({OverrideDir}); it may be AOT-only, merged, or not fast-deployed. Deploy a Debug build.");
            string local = Path.Combine(pulled, dll);
            await CatToLocalAsync(remote, local, ct).ConfigureAwait(false);
            string woven = Path.Combine(wovenDir, dll);
            var result = weaver.Weave(local, woven, resolver);
            if (result.Methods.Count == 0) { File.Delete(woven); continue; }
            deployedNow.Add((remote, woven));
        }
        if (weaver.Map.Count == 0)
            throw new ToolException($"The weave filter matched no method in {string.Join(", ", assemblies)}.");

        // Deploy collector first (so the woven references resolve), then the woven assemblies.
        collectorPath ??= DefaultCollectorPath;
        if (!File.Exists(collectorPath))
            throw new ToolException($"Collector assembly not found at {collectorPath}");
        await PushIntoOverrideAsync(collectorPath, $"{OverrideDir}/{CecilWeaver.CollectorAssemblyName}.dll", backup: false, ct).ConfigureAwait(false);
        _collectorDeployed = true;
        foreach (var (remote, woven) in deployedNow)
        {
            await PushIntoOverrideAsync(woven, remote, backup: true, ct).ConfigureAwait(false);
            LastSkippedAccessorCount = weaver.SkippedAccessorCount;
            LastAsyncStubCount = weaver.AsyncStubCount;
            LastAsyncBodyCount = weaver.AsyncBodyCount;
            _deployed.Add(remote);
            // The original .pdb no longer matches the rewritten assembly; move it aside
            // for the duration of the session (a stale pdb can upset the debugger
            // component that Debug builds load).
            string pdb = remote[..^4] + ".pdb";
            if (await RemoteExistsAsync(pdb, ct).ConfigureAwait(false))
            {
                try
                {
                    await _adb.RunAsAsync(_serial, _package, $"chmod 600 {pdb} 2>/dev/null; mv {pdb} {pdb}.naporig", ct).ConfigureAwait(false);
                    _movedPdbs.Add(pdb);
                }
                catch { /* not fatal */ }
            }
        }
        return weaver.Map;
    }

    /// <summary>Restore the original assemblies and remove the collector.</summary>
    public async Task RestoreAsync(CancellationToken ct)
    {
        foreach (var remote in _deployed)
        {
            try { await RestoreOneAsync(remote, ct).ConfigureAwait(false); }
            catch (Exception e) { RestoreErrors.Add($"{remote}: {e.Message}"); }
        }
        _deployed.Clear();
        foreach (var pdb in _movedPdbs)
        {
            try { await _adb.RunAsAsync(_serial, _package, $"test -f {pdb}.naporig && mv {pdb}.naporig {pdb}", ct).ConfigureAwait(false); }
            catch (Exception e) { RestoreErrors.Add($"{pdb}: {e.Message}"); }
        }
        _movedPdbs.Clear();
        if (_collectorDeployed)
        {
            try { await _adb.RunAsAsync(_serial, _package, $"rm -f {OverrideDir}/{CecilWeaver.CollectorAssemblyName}.dll", ct).ConfigureAwait(false); }
            catch { }
            _collectorDeployed = false;
        }
    }

    /// <summary>Restore one assembly from its .naporig backup (removes the backup on success).</summary>
    private async Task RestoreOneAsync(string remote, CancellationToken ct)
    {
        string backup = remote + ".naporig";
        // chmod first: the files are 0400, and a plain mv/rm can fail on some devices.
        string cmd = $"chmod 600 {remote} 2>/dev/null; chmod 600 {backup} 2>/dev/null; " +
                     $"if [ -f {backup} ]; then cp {backup} {remote} && rm -f {backup} && chmod 400 {remote} && echo RESTORED; else echo NOBACKUP; fi";
        string outp = await _adb.RunAsAsync(_serial, _package, cmd, ct).ConfigureAwait(false);
        if (!outp.Contains("RESTORED", StringComparison.Ordinal))
            throw new ToolException($"restore did not confirm ({outp.Trim()})");
    }

    /// <summary>Failures encountered by the last <see cref="RestoreAsync"/> (empty = clean).</summary>
    public List<string> RestoreErrors { get; } = new();

    /// <summary>Pull every *.napw event file the collector wrote into <paramref name="remoteEventsDir"/> to <paramref name="localDir"/>.</summary>
    public async Task<int> PullEventsAsync(string remoteEventsDir, string localDir, CancellationToken ct)
    {
        Directory.CreateDirectory(localDir);
        var ls = await _adb.RunAsAsync(_serial, _package, $"ls {remoteEventsDir}", ct).ConfigureAwait(false);
        // The types file maps allocation type ids to names; it travels with the events.
        var files = ls.Split('\n').Select(l => l.Trim())
            .Where(l => l.EndsWith(".napw", StringComparison.Ordinal) || l == "nap-types.txt")
            .ToList();
        foreach (var f in files)
            await CatToLocalAsync($"{remoteEventsDir}/{f}", Path.Combine(localDir, f), ct).ConfigureAwait(false);
        return files.Count(f => f.EndsWith(".napw", StringComparison.Ordinal));
    }

    public bool HasPendingChanges => _deployed.Count > 0 || _collectorDeployed || _movedPdbs.Count > 0;

    /// <summary>Property accessors skipped by the last weave.</summary>
    public int LastSkippedAccessorCount { get; private set; }

    /// <summary>Async stubs woven by the last weave (their time is the synchronous part only).</summary>
    public int LastAsyncStubCount { get; private set; }

    /// <summary>Async state machines woven by the last weave ("... (async body)" entries).</summary>
    public int LastAsyncBodyCount { get; private set; }

    /// <summary>Marker the collector writes the first time a woven method runs.</summary>
    public string MarkerPath => "files/nap-collector-loaded.txt";

    /// <summary>Path of the collector's pause/resume file inside the app's private files.</summary>
    public string ControlPath => "files/" + ControlFileName;

    /// <summary>Must match NetAndroidProfiler.Collector.Profiler.ControlFileName.</summary>
    public const string ControlFileName = "nap-control.txt";

    /// <summary>
    /// Pause or resume collection in the running app. The collector polls this file once a
    /// second, so a pause takes effect within about a second; the app keeps running and the
    /// woven methods stay woven (the overhead of the instrumentation remains).
    /// </summary>
    public Task SetCollectingAsync(bool collecting, CancellationToken ct) =>
        _adb.RunAsAsync(_serial, _package, $"echo {(collecting ? "run" : "pause")} > {ControlPath}", ct);

    /// <summary>Throw away the events collected so far on the device (clear results).</summary>
    public Task ClearEventsAsync(CancellationToken ct) =>
        _adb.RunAsAsync(_serial, _package, $"rm -f {RemoteEventsDir}/*.napw", ct);

    /// <summary>Remove a stale marker from a previous session.</summary>
    public Task ClearCollectorMarkerAsync(CancellationToken ct) =>
        _adb.RunAsync(_serial, ["shell", $"run-as {_package} rm -f {MarkerPath}"], ct);

    /// <summary>
    /// Wait until the collector reports that woven code is executing. When the app
    /// loads its assemblies from inside the APK (EmbedAssembliesIntoApk=true) the
    /// woven copies in the override directory are ignored and this never appears.
    /// </summary>
    public async Task<bool> WaitForCollectorMarkerAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await RemoteExistsAsync(MarkerPath, ct).ConfigureAwait(false)) return true;
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
        return false;
    }

    public string RemoteEventsDir => $"files/nap-events";

    /// <summary>Synchronous pull of one dll from the override dir (for the Cecil resolver callback). Returns false when absent.</summary>
    private bool PullFromOverrideSync(string dllName, string localPath, CancellationToken ct)
    {
        try
        {
            string remote = $"{OverrideDir}/{dllName}";
            if (!RemoteExistsAsync(remote, ct).GetAwaiter().GetResult()) return false;
            CatToLocalAsync(remote, localPath, ct).GetAwaiter().GetResult();
            return true;
        }
        catch { return false; }
    }

    private async Task<bool> RemoteExistsAsync(string relPath, CancellationToken ct)
    {
        var r = await _adb.RunAsync(_serial, ["shell", $"run-as {_package} ls {relPath}"], ct).ConfigureAwait(false);
        return r.Success && !r.StdOut.Contains("No such", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Copy a file out of the app sandbox, verifying that the payload is complete:
    /// a truncated read was observed once (2 KB of a 6656-byte assembly), which then
    /// corrupts everything downstream, so the size is checked against stat and the read is
    /// retried. /data/local/tmp cannot be used for staging: the app user cannot write there.
    ///
    /// A snapshot pulls files the app is still appending to, so "complete" cannot mean
    /// "exactly the size stat reported before the read": the file legitimately grows in
    /// between. Short is a truncation and is retried; longer is growth and is kept.
    /// </summary>
    private async Task CatToLocalAsync(string relPath, string local, CancellationToken ct)
    {
        long expected = await RemoteSizeAsync(relPath, ct).ConfigureAwait(false);
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            var data = await _adb.ExecOutAsync(_serial, $"run-as {_package} cat {relPath}", ct).ConfigureAwait(false);
            if (expected == 0 || data.Length >= expected)
            {
                await File.WriteAllBytesAsync(local, data, ct).ConfigureAwait(false);
                return;
            }
            // Perhaps it simply grew and shrank? No: files are append-only. Re-stat, in case
            // the first stat was taken before a burst and the read caught an earlier state.
            expected = await RemoteSizeAsync(relPath, ct).ConfigureAwait(false);
        }
        throw new ToolException($"Pull of {relPath} kept returning a truncated payload (expected {expected} bytes)");
    }

    /// <summary>Size of a file inside the app sandbox, 0 when unknown.</summary>
    private async Task<long> RemoteSizeAsync(string relPath, CancellationToken ct)
    {
        try
        {
            string outp = await _adb.RunAsAsync(_serial, _package, $"stat -c %s {relPath}", ct).ConfigureAwait(false);
            return long.TryParse(outp.Trim(), out long n) ? n : 0;
        }
        catch { return 0; }
    }

    private async Task PushIntoOverrideAsync(string localFile, string remoteRel, bool backup, CancellationToken ct)
    {
        string tmp = $"/data/local/tmp/nap-{Guid.NewGuid():N}";
        await _adb.PushAsync(_serial, localFile, tmp, ct).ConfigureAwait(false);
        await _adb.ShellAsync(_serial, $"chmod 644 {tmp}", ct).ConfigureAwait(false);
        string backupCmd = backup ? $"(test -f {remoteRel}.naporig || cp {remoteRel} {remoteRel}.naporig) && " : "";
        await _adb.RunAsAsync(_serial, _package, $"{backupCmd}rm -f {remoteRel} && cp {tmp} {remoteRel} && chmod 400 {remoteRel}", ct).ConfigureAwait(false);
        await _adb.RunAsync(_serial, ["shell", $"rm -f {tmp}"], ct).ConfigureAwait(false);
    }
}
