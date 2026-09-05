namespace NetAndroidDebugger.Core;

/// <summary>Lifecycle of a <see cref="DebugSession"/>.</summary>
public enum SessionState
{
    NotStarted,
    Deploying,
    Launching,
    /// <summary>At least one debuggee process is attached and none is stopped.</summary>
    Running,
    /// <summary>At least one debuggee process is stopped (breakpoint, step, pause, exception).</summary>
    Stopped,
    /// <summary>All debuggee processes are gone or the session was terminated.</summary>
    Exited,
}

/// <summary>Why a debuggee process stopped.</summary>
public enum StopReason
{
    Breakpoint,
    Step,
    Pause,
    Exception,
    UnhandledException,
}

/// <summary>An adb device as listed by <c>adb devices -l</c>.</summary>
public sealed record DeviceInfo(string Serial, string State, string? Model, bool IsEmulator);

/// <summary>The application to debug. Nothing here is specific to a test app.</summary>
/// <param name="PackageName">Android package (ApplicationId).</param>
/// <param name="ActivityName">Launcher activity (<c>pkg/fully.qualified.Name</c>); resolved via adb when null.</param>
/// <param name="ProjectPath">Android csproj used for deployment (<c>-t:Install</c>); optional.</param>
public sealed record AppTarget(string PackageName, string? ActivityName = null, string? ProjectPath = null);

/// <summary>A .NET for Android application project that can be launched.</summary>
/// <param name="ProjectPath">Absolute path to the <c>.csproj</c>.</param>
/// <param name="ApplicationId">The package name the project declares, or null when it is set outside the project file.</param>
/// <param name="TargetFramework">The Android framework it targets, e.g. <c>net9.0-android35.0</c>.</param>
/// <param name="InSolution">Whether a solution beside it names it. A project that merely sits in the tree is usually not the one meant.</param>
public sealed record AppProjectInfo(string ProjectPath, string? ApplicationId, string TargetFramework, bool InSolution);

/// <summary>Launch parameters.</summary>
/// <param name="DeviceSerial">Mandatory adb serial. The engine never relies on the adb default device.</param>
/// <param name="BaseSdbPort">First TCP port; each additional debuggee process gets the next one (port rotation).</param>
/// <param name="Deploy">Run the msbuild Install target before launching (requires <see cref="AppTarget.ProjectPath"/>).</param>
/// <param name="Configuration">msbuild configuration used for deployment.</param>
/// <param name="ConnectTimeout">How long to wait for the first agent after <c>am start</c> (the Mono agent itself gives up after 30 s).</param>
/// <param name="AgentLogLevel">Value of <c>loglevel=</c> in <c>debug.mono.extra</c>.</param>
/// <param name="PropertyLifetime">Freshness window written into <c>debug.mono.extra</c> (device clock). The property is
/// device-global: any Mono app process that starts while it is fresh waits for a debugger on our port, so keep this
/// short (default 3 minutes) unless helper processes of the debuggee are expected to start later.</param>
/// <param name="KeepPropertyFresh">Rewrite the property periodically so it never expires while the session lives.
/// Needed when the debuggee starts processes long after launch (an on-demand service, a crash reporter): with the
/// default they read an expired property and run without a debugger. The cost is that the window in which another
/// Mono app can pick up our port stays open for the whole session.</param>
/// <param name="AdbPath">adb executable, or the SDK or platform-tools folder holding it. Null means
/// "find it": <c>NAD_ADB_PATH</c>, then the SDK the environment or the registry names, then PATH
/// (see <c>AdbLocator</c>). A path that is given but wrong is an error, never replaced by a guess.</param>
public sealed record LaunchOptions(
    string DeviceSerial,
    int BaseSdbPort = 10000,
    bool Deploy = false,
    string Configuration = "Debug",
    TimeSpan? ConnectTimeout = null,
    int AgentLogLevel = 0,
    TimeSpan? PropertyLifetime = null,
    bool KeepPropertyFresh = false,
    string? AdbPath = null)
{
    public TimeSpan EffectiveConnectTimeout => ConnectTimeout ?? TimeSpan.FromSeconds(25);
    public TimeSpan EffectivePropertyLifetime => PropertyLifetime ?? TimeSpan.FromMinutes(3);
}

/// <summary>A debuggee process attached to the session.</summary>
public sealed record ProcessSnapshot(int Pid, string Name, int SdbPort, bool IsStopped, bool HasExited);

/// <summary>A source position in the debuggee.</summary>
public sealed record SourceLocationInfo(string? File, int Line, int Column, string Method);

/// <summary>A stop of one debuggee process.</summary>
public sealed record StopEvent(
    long Generation,
    int Pid,
    StopReason Reason,
    long ThreadId,
    SourceLocationInfo? Location,
    string? ExceptionType,
    string? Message);

/// <summary>A source-line breakpoint request.</summary>
/// <param name="File">Absolute source path, as compiled into the PDB.</param>
/// <param name="Line">1-based line.</param>
/// <param name="Condition">Stop only when this C# expression is true.</param>
/// <param name="HitCount">Stop from the Nth hit on (0 = every hit). Ignored when
/// <paramref name="HitCondition"/> is given.</param>
/// <param name="HitCondition">Richer form of the same idea: <c>"5"</c> or <c>">=5"</c> from the
/// fifth hit, <c>">5"</c> after it, <c>"=5"</c> only on it, <c>"%5"</c> every fifth.</param>
/// <param name="LogMessage">Turns the breakpoint into a logpoint: the app is NOT suspended, the
/// message is written to the debugger output with each <c>{expression}</c> evaluated in place.
/// On a phone this is often the only usable form - suspending an app that talks to a backend
/// makes it time out (see ANDROID_ATTACH_NOTES.md, the reference application).</param>
public sealed record BreakpointSpec(
    string File,
    int Line,
    string? Condition = null,
    int HitCount = 0,
    string? HitCondition = null,
    string? LogMessage = null);

/// <summary>A breakpoint as stored by the session. <see cref="Verified"/> is true when at least one process resolved it.</summary>
public sealed record BreakpointInfo(int Id, BreakpointSpec Spec, bool Verified, string? Message);

/// <summary>Exception break settings.</summary>
/// <param name="BreakOnUnhandled">Stop on unhandled managed exceptions.</param>
/// <param name="FirstChanceTypes">Fully qualified exception type names to stop on when thrown (subclasses included).</param>
public sealed record ExceptionFilters(bool BreakOnUnhandled = true, IReadOnlyList<string>? FirstChanceTypes = null);

/// <summary>What to do with an exception that matches a rule.</summary>
public enum ExceptionAction
{
    /// <summary>Suspend the app and report the stop, as an exception filter would.</summary>
    Break,
    /// <summary>Write a line to the debugger output and let the app carry on.</summary>
    Log,
    /// <summary>Write a line plus the stack, and let the app carry on.</summary>
    LogStack,
    /// <summary>Let the app carry on, silently.</summary>
    Ignore,
}

/// <summary>
/// One rule of the per-exception engine. The criteria that are set are AND-ed; an unset one is a
/// wildcard, so a rule with only an action matches everything. Rules are ordered and the first
/// match wins, which is what makes "ignore this one, break on the rest" expressible.
/// <para>
/// This exists because a real app throws constantly on purpose: the reference application raises MQTT timeouts
/// whenever the network blinks and a handled InvalidOperationException on every reconnect. An
/// all-or-nothing filter is unusable there — the choice is between no exceptions and a stop every
/// few seconds.
/// </para>
/// </summary>
/// <param name="Type">Exact runtime type name, e.g. <c>System.InvalidOperationException</c>.</param>
/// <param name="TypeContains">Substring of the runtime type name, for matching a namespace.</param>
/// <param name="MessageContains">Substring of the exception message. Reading the message needs a
/// call into the debuggee, so a rule using it costs more than one matching on type alone.</param>
/// <param name="MessageRegex">Regular expression over the message. Same cost as the substring.</param>
/// <param name="SourceFileContains">Substring of the file the exception was raised in, matched
/// against the topmost frame that has source.</param>
/// <param name="Action">What to do when every set criterion matches.</param>
public sealed record ExceptionRule(
    ExceptionAction Action,
    string? Type = null,
    string? TypeContains = null,
    string? MessageContains = null,
    string? MessageRegex = null,
    string? SourceFileContains = null);

public sealed record ThreadSnapshot(int Pid, long Id, string Name, string? Location, bool IsStopped);

public sealed record FrameSnapshot(int Index, string Method, string? File, int Line, int Column, bool IsExternal, bool HasDebugInfo);

/// <summary>A value. <see cref="ExpansionHandle"/> is valid until the owning process resumes.</summary>
public sealed record VariableSnapshot(
    string Name,
    string TypeName,
    string Value,
    string DisplayValue,
    bool HasChildren,
    string? ExpansionHandle,
    bool IsError);

/// <summary>One assembly the debuggee has loaded.</summary>
/// <param name="HasSymbols">
/// Whether the runtime has debug information for it, or null when the debuggee's protocol version
/// cannot answer. The first thing to check when a breakpoint stays pending: without symbols no line
/// in that assembly can ever bind, and comparing source paths is wasted effort.
/// </param>
public sealed record AssemblyInfo(int Pid, string Name, string? Path, bool? HasSymbols = null);

/// <summary>
/// A source file the debuggee's runtime knows about, with the exact path compiled into the PDB.
/// </summary>
/// <param name="Pid">Process whose runtime reported it.</param>
/// <param name="Path">Path as the runtime has it — this is what a breakpoint must match.</param>
/// <param name="Types">Types compiled from that file (truncated for very large files).</param>
public sealed record SourceFileInfo(int Pid, string Path, IReadOnlyList<string> Types);

/// <summary>
/// One line of debuggee output: a logcat line of one of the app's processes, or text the
/// debuggee wrote to stdout/stderr (delivered through the debugger, tagged <c>stdout</c>/<c>stderr</c>).
/// </summary>
/// <param name="Level">Android priority: V, D, I, W, E or F.</param>
public sealed record AppLogLine(DateTime Timestamp, int Pid, int Tid, char Level, string Tag, string Message)
{
    /// <summary>Compact rendering: <c>12:34:56.789 E/Tag(1234): message</c>.</summary>
    public override string ToString() => $"{Timestamp:HH:mm:ss.fff} {Level}/{Tag}({Pid}): {Message}";
}

public sealed record SessionStatus(
    SessionState State,
    long StopGeneration,
    StopEvent? LastStop,
    string? DeviceSerial,
    string? PackageName,
    IReadOnlyList<ProcessSnapshot> Processes,
    string? LastError);

/// <summary>Thrown when an operation is not valid in the current <see cref="SessionState"/>.</summary>
public sealed class InvalidSessionStateException(string message) : InvalidOperationException(message);

/// <summary>Thrown when adb or the launch orchestration fails.</summary>
public sealed class LaunchException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A source of exception rules the engine can re-read while a session is live — in practice a file
/// the user edits. Core owns *when* to reload (on resume, if it changed) and knows nothing about
/// the format: reading it is the frontend's job, which is what keeps JSON out of the engine.
/// </summary>
public interface IExceptionRuleSource
{
    /// <summary>Where the rules come from, for log messages. A path, usually.</summary>
    string Description { get; }

    /// <summary>
    /// When the source last changed, or null when it does not exist. The engine compares this
    /// against the value it saw at load time, so a source that cannot tell is simply never
    /// reloaded rather than reloaded on every resume.
    /// </summary>
    DateTime? LastChangedUtc { get; }

    /// <summary>Reads the rules. Throwing here is reported and leaves the previous set in force.</summary>
    IReadOnlyList<ExceptionRule> Load();
}
