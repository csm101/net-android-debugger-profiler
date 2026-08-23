using System;
using System.Collections.Generic;
using System.Diagnostics; // Stopwatch only
using System.IO;
using System.Threading;

namespace NetAndroidProfiler.Collector;

/// <summary>
/// Runtime target of the IL weaver: woven methods call
/// <see cref="Enter"/> / <see cref="Leave"/> / <see cref="ExceptionLeave"/>.
///
/// Disabled (near-zero cost: one static bool check) unless the environment
/// variable NAP_PROFILER_OUT names a writable directory. When enabled, each
/// thread appends fixed-size records to its own file
/// "&lt;pid&gt;-t&lt;managedThreadId&gt;.napw" in that directory:
///
///   header: "NAPW" 0x01, i64 Stopwatch.Frequency, i32 pid, i32 threadId
///   record: u8 kind (1 enter, 2 leave, 3 exception-leave), i32 methodId, i64 stopwatchTicks
///
/// All writers are flushed once per second and on process exit; a killed
/// process loses at most the last second of events.
/// </summary>
public static class Profiler
{
    public const byte FormatVersion = 1;
    public const byte KindEnter = 1;
    public const byte KindLeave = 2;
    public const byte KindExceptionLeave = 3;
    /// <summary>Allocation: the record's id field is a type id, resolved through the types file.</summary>
    public const byte KindAllocation = 4;

    /// <summary>Name of the file mapping allocation type ids to type names.</summary>
    public const string TypesFileName = "nap-types.txt";

    private static readonly string? OutDir;
    private static readonly bool Enabled;
    private static readonly string ProcessToken = Guid.NewGuid().ToString("N").Substring(0, 8);
    private static readonly List<ThreadWriter> Writers = new List<ThreadWriter>();
    [ThreadStatic] private static ThreadWriter? t_writer;

    /// <summary>
    /// Environment variable choosing what a call costs: "tree" (default) keeps a calling
    /// context tree in the process and writes nodes, "trace" writes a record per enter and
    /// per leave. Trace keeps the order calls happened in and every single duration; tree
    /// keeps what a call tree is made of, for a fraction of the cost per call.
    /// </summary>
    public const string ModeVariable = "NAP_PROFILER_MODE";

    private static readonly bool TreeMode;
    private static readonly List<CallTree> Trees = new List<CallTree>();
    [ThreadStatic] private static CallTree? t_tree;

    /// <summary>Rooted so the periodic flush keeps running for the life of the process.</summary>
    private static Timer? s_flushTimer;

    /// <summary>
    /// Name of the file the profiler writes next to the marker to pause and resume
    /// collection without stopping the app ("pause" or "run"): this is what makes the GUI's
    /// pause button real on the weaver engine. Polled on the flush tick, so it costs one
    /// small read per second and nothing on the instrumented path.
    /// </summary>
    public const string ControlFileName = "nap-control.txt";

    private static volatile bool s_paused;
    private static string? s_controlPath;

    /// <summary>
    /// Bumped when the profiler clears the results: the event files are deleted on the
    /// device, and a writer that kept appending to its old handle would be writing into a
    /// file nobody can see any more (a deleted file stays alive for whoever holds it open).
    /// Every thread notices the change on its next event and starts a fresh file.
    /// </summary>
    private static volatile int s_generation;

    /// <summary>False while collection is paused; woven methods then return immediately.</summary>
    public static bool Collecting => Enabled && !s_paused;

    /// <summary>
    /// Name of the marker file written next to the app's private files as soon as
    /// this type is loaded (i.e. the first time a woven method runs), regardless of
    /// whether profiling is enabled. It is the diagnostic that tells "the woven code
    /// never ran / the collector never loaded" apart from "it loaded but the output
    /// directory was not configured".
    /// </summary>
    public const string MarkerFileName = "nap-collector-loaded.txt";

    /// <summary>An environment read that can never throw the app down.</summary>
    private static string? ReadVariable(string name)
    {
        try { return Environment.GetEnvironmentVariable(name); } catch { return null; }
    }

    static Profiler()
    {
        // Never use System.Diagnostics.Process here: on Android it can throw and
        // would silently disable the collector. The pid field is cosmetic; files
        // are keyed by a per-process token + managed thread id.
        string? dir = null;
        try { dir = Environment.GetEnvironmentVariable("NAP_PROFILER_OUT"); } catch { }
        WriteMarker(dir);
        try
        {
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir!);
            OutDir = dir;
            Enabled = true;
            string? mode = null;
            try { mode = Environment.GetEnvironmentVariable(ModeVariable); } catch { }
            TreeMode = !string.Equals(mode, "trace", StringComparison.OrdinalIgnoreCase);
            // The timer must be rooted in a static field: GC.KeepAlive only reaches the end
            // of this constructor, so a local would be collected and every buffered event
            // would then sit in its 64 KB stream until the buffer filled - which for a small
            // weave scope never happens, and the session reads empty files.
            s_flushTimer = new Timer(_ => { FlushAll(); PollControl(); }, null, 1000, 1000);
            string? markerDir = null;
            try { markerDir = Environment.GetEnvironmentVariable("NAP_PROFILER_MARKER_DIR"); } catch { }
            s_controlPath = Path.Combine(string.IsNullOrEmpty(markerDir) ? dir! : markerDir!, ControlFileName);
            PollControl();
            AppDomain.CurrentDomain.ProcessExit += (_, __) => FlushAll();
            AppDomain.CurrentDomain.DomainUnload += (_, __) => FlushAll();
        }
        catch
        {
            // Never break the app because profiling could not initialize.
            Enabled = false;
        }
    }

    /// <summary>
    /// Best-effort marker written when this type loads: says whether the collector
    /// code is running at all inside the app and what it saw in the environment.
    /// Written to NAP_PROFILER_MARKER_DIR (set by the engine to the app's private
    /// files directory) and to the output directory when configured.
    ///
    /// Only plain file I/O and environment reads are allowed here: this runs from a
    /// static constructor on whatever thread first executes a woven method, often the
    /// UI thread during Activity.OnCreate. Anything that calls into Java through JNI
    /// (e.g. Environment.GetFolderPath) can deadlock the app while the Android runtime
    /// bridge is still initializing - observed hanging TestTarget.
    /// </summary>
    private static void WriteMarker(string? outDir)
    {
        string text;
        try
        {
            text = string.Join(Environment.NewLine,
                "collector=" + typeof(Profiler).Assembly.FullName,
                "NAP_PROFILER_OUT=" + (string.IsNullOrEmpty(outDir) ? "<unset>" : outDir),
                ModeVariable + "=" + (ReadVariable(ModeVariable) ?? "<unset>"),
                "utc=" + DateTime.UtcNow.ToString("O"),
                "stopwatchFrequency=" + Stopwatch.Frequency);
        }
        catch { text = "collector loaded"; }

        string? markerDir = null;
        try { markerDir = Environment.GetEnvironmentVariable("NAP_PROFILER_MARKER_DIR"); } catch { }
        foreach (string? dir in new[] { markerDir, outDir })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            try
            {
                Directory.CreateDirectory(dir!);
                File.WriteAllText(Path.Combine(dir!, MarkerFileName), text);
            }
            catch { /* diagnostics must never break the app */ }
        }
    }

    public static void Enter(int methodId)
    {
        if (!Collecting) return;
        if (TreeMode) { Tree()?.Enter(methodId, Stopwatch.GetTimestamp()); return; }
        Write(KindEnter, methodId);
    }

    public static void Leave(int methodId)
    {
        if (!Collecting) return;
        if (TreeMode) { Tree()?.Leave(methodId, Stopwatch.GetTimestamp()); return; }
        Write(KindLeave, methodId);
    }

    /// <summary>This thread's call tree, created on its first instrumented call.</summary>
    private static CallTree? Tree()
    {
        var t = t_tree;
        if (t is not null && t.Generation == s_generation) return t;
        try
        {
            t = new CallTree(Thread.CurrentThread.ManagedThreadId, s_generation);
            lock (Trees) Trees.Add(t);
            return t_tree = t;
        }
        catch { return null; }
    }

    public static void ExceptionLeave(int methodId)
    {
        if (!Collecting) return;
        Write(KindExceptionLeave, methodId);
    }

    /// <summary>
    /// Called right after a woven <c>newobj</c>/<c>newarr</c>: records one allocation of
    /// the given type. The instance itself is not touched or retained - only its type
    /// matters - and the type id is resolved through the types file the collector writes
    /// next to the event files.
    /// </summary>
    public static void Allocated(RuntimeTypeHandle handle)
    {
        if (!Collecting) return;
        Write(KindAllocation, TypeId(handle));
    }

    private static readonly Dictionary<RuntimeTypeHandle, int> TypeIds = new();
    private static readonly object TypesLock = new object();

    private static int TypeId(RuntimeTypeHandle handle)
    {
        lock (TypesLock)
        {
            if (TypeIds.TryGetValue(handle, out int id)) return id;
            id = TypeIds.Count + 1;
            TypeIds[handle] = id;
            try
            {
                string name = Type.GetTypeFromHandle(handle)?.FullName ?? "<unknown>";
                File.AppendAllText(Path.Combine(OutDir!, TypesFileName), id + "\t" + name + Environment.NewLine);
            }
            catch { /* the id stays usable, only its name is missing */ }
            return id;
        }
    }

    private static void Write(byte kind, int methodId)
    {
        try
        {
            var w = t_writer;
            if (w is null || w.Generation != s_generation)
                w = t_writer = NewWriter();
            w?.Write(kind, methodId, Stopwatch.GetTimestamp());
        }
        catch
        {
            t_writer = ThreadWriter.Broken;
        }
    }

    private static ThreadWriter? NewWriter()
    {
        var w = ThreadWriter.Open(OutDir!, ProcessToken, Thread.CurrentThread.ManagedThreadId, s_generation);
        if (w is null) return ThreadWriter.Broken;
        lock (Writers) Writers.Add(w);
        return w;
    }

    /// <summary>
    /// Read the pause/resume file written by the profiler. Absent or unreadable means
    /// "collect": a control channel that fails must never silently stop a session.
    /// </summary>
    private static void PollControl()
    {
        if (s_controlPath is null) return;
        try
        {
            if (!File.Exists(s_controlPath)) { s_paused = false; return; }
            string text = File.ReadAllText(s_controlPath).Trim();
            s_paused = text.StartsWith("pause", StringComparison.OrdinalIgnoreCase);
            int at = text.IndexOf("gen=", StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && int.TryParse(text.Substring(at + 4).Split(' ')[0], out int generation) && generation != s_generation)
            {
                s_generation = generation;
                lock (Writers)
                {
                    foreach (var w in Writers) w.Close();
                    Writers.Clear();
                }
                // Clearing means "forget what you have": the trees go with the event files,
                // and every thread starts a new one on its next instrumented call.
                lock (Trees) Trees.Clear();
            }
        }
        catch { /* keep the previous state */ }
    }

    /// <summary>Flush every thread's buffer to disk (also called by the 1 s timer).</summary>
    public static void FlushAll()
    {
        if (!Enabled) return;
        lock (Writers)
            foreach (var w in Writers)
                w.Flush();
        if (TreeMode) WriteTrees();
    }

    /// <summary>
    /// Writes each thread's tree as a whole file, replacing the previous one: the nodes are
    /// the state, not a log, so there is nothing to append and a reader always finds a
    /// complete file. The write goes to a temporary name and is renamed into place, so a
    /// profiler pulling files never reads a half-written tree.
    /// </summary>
    private static void WriteTrees()
    {
        CallTree[] trees;
        lock (Trees) trees = Trees.ToArray();
        foreach (var tree in trees)
        {
            try
            {
                var nodes = tree.Snapshot();
                int count = Volatile.Read(ref nodes.Count);
                string name = ProcessToken + "-t" + tree.ThreadId + "-g" + tree.Generation + ".napt";
                string finalPath = Path.Combine(OutDir!, name);
                string temporary = finalPath + ".writing";
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(new byte[] { (byte)'N', (byte)'A', (byte)'P', (byte)'T' });
                    writer.Write((byte)1);
                    writer.Write(Stopwatch.Frequency);
                    writer.Write(tree.ThreadId);
                    writer.Write(count);
                    for (int i = 0; i < count; i++)
                    {
                        writer.Write(nodes.Parent[i]);
                        writer.Write(nodes.Method[i]);
                        writer.Write(nodes.Calls[i]);
                        writer.Write(nodes.Inclusive[i]);
                        writer.Write(nodes.Exclusive[i]);
                        writer.Write(nodes.Min[i]);
                        writer.Write(nodes.Max[i]);
                    }
                }
                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(temporary, finalPath);
            }
            catch { /* a tree that cannot be written must not break the app */ }
        }
    }

    private sealed class ThreadWriter
    {
        /// <summary>Sentinel for a thread whose writer failed: all further writes are dropped.</summary>
        public static readonly ThreadWriter Broken = new ThreadWriter(null);

        /// <summary>Which generation of event files this writer belongs to.</summary>
        public int Generation;

        private readonly FileStream? _stream;
        private readonly byte[] _buffer = new byte[13];
        private readonly object _lock = new object();

        private ThreadWriter(FileStream? stream) { _stream = stream; }

        public static ThreadWriter? Open(string dir, string token, int threadId, int generation)
        {
            try
            {
                // The generation is part of the name so a pull in flight never sees a file
                // truncated under it: a cleared session simply starts writing new names.
                string name = generation == 0 ? $"{token}-t{threadId}.napw" : $"{token}-t{threadId}-g{generation}.napw";
                var fs = new FileStream(Path.Combine(dir, name), FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
                var header = new byte[4 + 1 + 8 + 4 + 4];
                header[0] = (byte)'N'; header[1] = (byte)'A'; header[2] = (byte)'P'; header[3] = (byte)'W';
                header[4] = FormatVersion;
                WriteInt64(header, 5, Stopwatch.Frequency);
                WriteInt32(header, 13, 0);
                WriteInt32(header, 17, threadId);
                fs.Write(header, 0, header.Length);
                return new ThreadWriter(fs) { Generation = generation };
            }
            catch
            {
                return null;
            }
        }

        public void Write(byte kind, int methodId, long ticks)
        {
            if (_stream is null) return;
            lock (_lock)
            {
                _buffer[0] = kind;
                WriteInt32(_buffer, 1, methodId);
                WriteInt64(_buffer, 5, ticks);
                _stream.Write(_buffer, 0, 13);
            }
        }

        public void Flush()
        {
            if (_stream is null) return;
            try { lock (_lock) _stream.Flush(); } catch { }
        }

        /// <summary>Flush and release the file: the next event of this thread opens a new one.</summary>
        public void Close()
        {
            if (_stream is null) return;
            try { lock (_lock) { _stream.Flush(); _stream.Dispose(); } } catch { }
        }

        private static void WriteInt32(byte[] b, int o, int v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }

        private static void WriteInt64(byte[] b, int o, long v)
        {
            for (int i = 0; i < 8; i++) b[o + i] = (byte)(v >> (8 * i));
        }
    }
}
