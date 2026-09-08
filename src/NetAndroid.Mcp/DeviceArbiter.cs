using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using DebugState = NetAndroidDebugger.Core.SessionState;
using ProfileState = NetAndroidProfiler.Core.Sessions.SessionState;

namespace NetAndroid.Mcp;

/// <summary>
/// Owns the device-global Mono state on behalf of both engines. A debug session holds
/// <see cref="DeviceGlobals.DebugMonoExtra"/> on its device for as long as it runs, a profiling
/// session <see cref="DeviceGlobals.DebugMonoProfile"/> and the app's override environment; an app
/// started under both would wait for a debugger and connect to a profiler at once, which neither
/// engine supports. So a call that would start one engine on a device the other holds is refused
/// before it reaches the tool, with the session to stop named in the answer. The one start that
/// touches nothing device-global is the profiler attaching to the process the debugger already
/// runs: that is allowed, it is how a debugged app gets profiled from a breakpoint on. Everything
/// else passes through untouched.
/// </summary>
public sealed class DeviceArbiter(NetAndroidDebugger.Mcp.SessionHost debugger, NetAndroidProfiler.Mcp.SessionHost profiler)
{
    /// <summary>The tools that start the debugger on a device.</summary>
    public static readonly IReadOnlySet<string> DebuggerStarts =
        new HashSet<string>(StringComparer.Ordinal) { "launch_app", "launch_from_config", "attach_to_app" };

    /// <summary>The tools that start a profiling session on a device.</summary>
    public static readonly IReadOnlySet<string> ProfilerStarts =
        new HashSet<string>(StringComparer.Ordinal) { "profile_run", "profile_start" };

    /// <summary>A profiling session that still holds device state.</summary>
    public readonly record struct Holder(string Id, string DeviceSerial);

    /// <summary>The device and package a debug session holds.</summary>
    public readonly record struct DebugHold(string DeviceSerial, string? PackageName);

    /// <summary>
    /// The decision itself, free of any live object: null when the call may proceed, otherwise
    /// the text refusing it. <paramref name="attachTo"/> is the package a profiler start attaches
    /// to without restarting it, null when it would restart the app. A call that names no device
    /// is refused whenever the other engine holds any device, because the tool would then pick
    /// one on its own.
    /// </summary>
    public static string? Refusal(string tool, string? requestedSerial, string? attachTo, DebugHold? debugging, IReadOnlyCollection<Holder> profiling)
    {
        if (ProfilerStarts.Contains(tool) && debugging is { } debug && Matches(requestedSerial, debug.DeviceSerial))
        {
            if (requestedSerial is not null && attachTo is not null && string.Equals(attachTo, debug.PackageName, StringComparison.Ordinal))
                return null;
            return $"A debug session is active on {debug.DeviceSerial} and holds {DeviceGlobals.DebugMonoExtra} there: an app started " +
                   "for profiling would wait for a debugger instead. Call stop_debugging first, or profile on another device, or " +
                   $"profile the debugged app itself in place (launch: attach, packageName: {debug.PackageName}) after " +
                   "remove_all_breakpoints and clearing the exception rules, so that nothing stops it while it is sampled.";
        }
        if (DebuggerStarts.Contains(tool))
        {
            var holder = profiling.FirstOrDefault(h => Matches(requestedSerial, h.DeviceSerial));
            if (holder.Id is not null)
                return $"Profiling session {holder.Id} is running on {holder.DeviceSerial} and holds {DeviceGlobals.DebugMonoProfile} " +
                       "and the app's override environment there: an app launched for debugging would connect to the profiler as " +
                       $"well. Call profile_stop for session {holder.Id} first, or debug on another device.";
        }
        return null;
    }

    /// <summary>The decision for the live state of both engines, read at call time.</summary>
    public string? Decide(string tool, string? requestedSerial, string? attachTo)
    {
        var debug = debugger.Current;
        DebugHold? debugging = null;
        if (debug is { State: not (DebugState.NotStarted or DebugState.Exited) } && debug.GetStatus() is { DeviceSerial: { } serial } status)
            debugging = new DebugHold(serial, status.PackageName);
        var profiling = profiler.LiveSessions
            .Where(live => live.Session.State is ProfileState.Preparing or ProfileState.WaitingForApp or ProfileState.Collecting or ProfileState.Analyzing)
            .Select(live => new Holder(live.Session.Id, live.Session.Spec.DeviceSerial))
            .ToList();
        return Refusal(tool, requestedSerial, attachTo, debugging, profiling);
    }

    /// <summary>The call filter: the start tools consult the arbiter, everything else passes through.</summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(McpRequestHandler<CallToolRequestParams, CallToolResult> next)
        => async (context, ct) =>
        {
            var tool = context.Params?.Name;
            if (tool is not null && (DebuggerStarts.Contains(tool) || ProfilerStarts.Contains(tool)))
            {
                var arbiter = context.Services!.GetRequiredService<DeviceArbiter>();
                if (arbiter.Decide(tool, Argument(context.Params, "deviceSerial"), AttachTarget(context.Params)) is { } refusal)
                    return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = refusal }] };
            }
            return await next(context, ct).ConfigureAwait(false);
        };

    private static bool Matches(string? requested, string held) =>
        requested is null || string.Equals(requested, held, StringComparison.Ordinal);

    private static string? AttachTarget(CallToolRequestParams? parameters) =>
        string.Equals(Argument(parameters, "launch"), "attach", StringComparison.OrdinalIgnoreCase) ? Argument(parameters, "packageName") : null;

    private static string? Argument(CallToolRequestParams? parameters, string name)
    {
        if (parameters?.Arguments is null || !parameters.Arguments.TryGetValue(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
