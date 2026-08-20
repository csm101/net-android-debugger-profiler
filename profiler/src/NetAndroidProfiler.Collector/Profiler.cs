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
    /// Name of the marker file written next to the app's private files as soon as
    /// this type is loaded (i.e. the first time a woven method runs), regardless of
    /// whether profiling is enabled. It is the diagnostic that tells "the woven code
    /// never ran / the collector never loaded" apart from "it loaded but the output
    /// directory was not configured".
    /// </summary>
    public const string MarkerFileName = "nap-collector-loaded.txt";

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
            var timer = new Timer(_ => FlushAll(), null, 1000, 1000);
            GC.KeepAlive(timer);
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
        if (!Enabled) return;
        Write(KindEnter, methodId);
    }

    public static void Leave(int methodId)
    {
        if (!Enabled) return;
        Write(KindLeave, methodId);
    }

    public static void ExceptionLeave(int methodId)
    {
        if (!Enabled) return;
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
        if (!Enabled) return;
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
            var w = t_writer ??= NewWriter();
            w?.Write(kind, methodId, Stopwatch.GetTimestamp());
        }
        catch
        {
            t_writer = ThreadWriter.Broken;
        }
    }

    private static ThreadWriter? NewWriter()
    {
        var w = ThreadWriter.Open(OutDir!, ProcessToken, Thread.CurrentThread.ManagedThreadId);
        if (w is null) return ThreadWriter.Broken;
        lock (Writers) Writers.Add(w);
        return w;
    }

    /// <summary>Flush every thread's buffer to disk (also called by the 1 s timer).</summary>
    public static void FlushAll()
    {
        if (!Enabled) return;
        lock (Writers)
            foreach (var w in Writers)
                w.Flush();
    }

    private sealed class ThreadWriter
    {
        /// <summary>Sentinel for a thread whose writer failed: all further writes are dropped.</summary>
        public static readonly ThreadWriter Broken = new ThreadWriter(null);

        private readonly FileStream? _stream;
        private readonly byte[] _buffer = new byte[13];
        private readonly object _lock = new object();

        private ThreadWriter(FileStream? stream) { _stream = stream; }

        public static ThreadWriter? Open(string dir, string token, int threadId)
        {
            try
            {
                var fs = new FileStream(Path.Combine(dir, $"{token}-t{threadId}.napw"), FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
                var header = new byte[4 + 1 + 8 + 4 + 4];
                header[0] = (byte)'N'; header[1] = (byte)'A'; header[2] = (byte)'P'; header[3] = (byte)'W';
                header[4] = FormatVersion;
                WriteInt64(header, 5, Stopwatch.Frequency);
                WriteInt32(header, 13, 0);
                WriteInt32(header, 17, threadId);
                fs.Write(header, 0, header.Length);
                return new ThreadWriter(fs);
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
