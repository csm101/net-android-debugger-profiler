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

/// <summary>Launch parameters.</summary>
/// <param name="DeviceSerial">Mandatory adb serial. The engine never relies on the adb default device.</param>
/// <param name="BaseSdbPort">First TCP port; each additional debuggee process gets the next one (port rotation).</param>
/// <param name="Deploy">Run the msbuild Install target before launching (requires <see cref="AppTarget.ProjectPath"/>).</param>
/// <param name="Configuration">msbuild configuration used for deployment.</param>
/// <param name="ConnectTimeout">How long to wait for the first agent after <c>am start</c> (the Mono agent itself gives up after 30 s).</param>
/// <param name="AgentLogLevel">Value of <c>loglevel=</c> in <c>debug.mono.extra</c>.</param>
/// <param name="PropertyLifetime">Freshness window written into <c>debug.mono.extra</c> (device clock).</param>
/// <param name="AdbPath">adb executable; defaults to <c>adb</c> on PATH.</param>
public sealed record LaunchOptions(
    string DeviceSerial,
    int BaseSdbPort = 10000,
    bool Deploy = false,
    string Configuration = "Debug",
    TimeSpan? ConnectTimeout = null,
    int AgentLogLevel = 0,
    TimeSpan? PropertyLifetime = null,
    string AdbPath = "adb")
{
    public TimeSpan EffectiveConnectTimeout => ConnectTimeout ?? TimeSpan.FromSeconds(25);
    public TimeSpan EffectivePropertyLifetime => PropertyLifetime ?? TimeSpan.FromMinutes(30);
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
public sealed record BreakpointSpec(string File, int Line, string? Condition = null, int HitCount = 0);

/// <summary>A breakpoint as stored by the session. <see cref="Verified"/> is true when at least one process resolved it.</summary>
public sealed record BreakpointInfo(int Id, BreakpointSpec Spec, bool Verified, string? Message);

/// <summary>Exception break settings.</summary>
/// <param name="BreakOnUnhandled">Stop on unhandled managed exceptions.</param>
/// <param name="FirstChanceTypes">Fully qualified exception type names to stop on when thrown (subclasses included).</param>
public sealed record ExceptionFilters(bool BreakOnUnhandled = true, IReadOnlyList<string>? FirstChanceTypes = null);

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

public sealed record AssemblyInfo(int Pid, string Name, string? Path);

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
