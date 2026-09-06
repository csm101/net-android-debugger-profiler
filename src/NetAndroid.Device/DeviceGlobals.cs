namespace NetAndroid.Device;

/// <summary>
/// The device-global names both products touch. They are global on purpose: the Mono runtime
/// reads them at the start of every .NET app process on the device, whoever wrote them.
/// <list type="bullet">
/// <item><see cref="DebugMonoExtra"/> is written by the debugger (its launcher publishes the SDB
/// agent address and a deadline in it, rotates it per process, and restores it at shutdown).</item>
/// <item><see cref="DebugMonoProfile"/> is written by the profiler (the diagnostics port for apps
/// that have no per-app environment file, restored at session end).</item>
/// <item><see cref="OverrideEnvironmentPath"/> is the per-app environment override file the
/// profiler edits for debuggable apps, with backup and restore; the debugger only reads the
/// assemblies the .NET Android fast deployment puts in the same folder.</item>
/// </list>
/// Running both products on the same app at the same time is not coordinated yet: each one
/// notices a mark the other left (<see cref="DevicePropertyOverride.ForeignValueWarning"/>) and
/// takes the property over, but nothing arbitrates. That coordination is the unified MCP server's
/// job (docs/ARCHITECTURE.md, decision 2), which is why these names live in one place.
/// </summary>
public static class DeviceGlobals
{
    /// <summary>The debugger's property: <c>debug=127.0.0.1:port,timeout=deviceEpoch,loglevel=n,server=y</c>.</summary>
    public const string DebugMonoExtra = "debug.mono.extra";

    /// <summary>The profiler's property: the value of <c>DOTNET_DiagnosticPorts</c> for every .NET app on the device.</summary>
    public const string DebugMonoProfile = "debug.mono.profile";

    /// <summary>
    /// The app's environment override file, relative to its data directory, loaded by the Debug
    /// flavor of libmonodroid after the baked environment (dotnet/android
    /// <c>src/native/mono/runtime-base/android-system.cc</c>).
    /// </summary>
    public static string OverrideEnvironmentPath(string abi) => $"files/.__override__/{abi}/environment";
}
