using System.Text.RegularExpressions;

namespace NetAndroidProfiler.Core.Analysis;

/// <summary>
/// Decides whether a leaf frame denotes a thread that is blocked rather than
/// running. The Mono sample profiler samples every managed thread, including
/// those parked in Sleep/Wait; samples whose leaf frame matches here are
/// counted as wall-clock time but excluded from the *Cpu statistics.
/// </summary>
public sealed class WaitFrameClassifier
{
    private readonly Regex _pattern;

    /// <summary>Default patterns: runtime wait/sleep PInvoke leaves and the managed wait primitives.</summary>
    public static readonly string[] DefaultPatterns =
    [
        @"^Interop\.Sys\..*(Wait|Sleep|Poll|EPoll|Read|Accept|Recv|Select)",
        @"LowLevelMonitor",
        @"^System\.Threading\.Monitor\.(Wait|ObjWait)",
        @"^System\.Threading\.WaitHandle\.Wait",
        @"^System\.Threading\.Thread\.Sleep",
        @"^System\.Threading\.ManualResetEventSlim\.Wait",
        @"^System\.Threading\.SemaphoreSlim\.Wait",
        @"^System\.Threading\.Tasks\.Task\.(Wait|InternalWait)",
    ];

    public WaitFrameClassifier(IEnumerable<string>? patterns = null)
    {
        var list = (patterns ?? DefaultPatterns).ToArray();
        _pattern = new Regex(string.Join("|", list.Select(p => $"(?:{p})")), RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }

    public static WaitFrameClassifier Default { get; } = new();

    /// <summary>True when <paramref name="fullMethodName"/> (Namespace.Type.Method(sig)) is a wait frame.</summary>
    public bool IsWaitFrame(string fullMethodName) => _pattern.IsMatch(fullMethodName);
}
