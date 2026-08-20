using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using NetAndroidDebugger.Core;

namespace NetAndroidDebugger.Mcp;

/// <summary>
/// MCP tool surface. Thin translation layer: every tool maps to one or two
/// <see cref="DebugSession"/> calls and renders plain text for the model.
/// </summary>
[McpServerToolType]
public sealed class DebuggerTools(SessionHost host)
{
    private static TimeSpan Secs(int? s, int dflt) => TimeSpan.FromSeconds(s is > 0 ? s.Value : dflt);

    // ------------------------------------------------------------------ lifecycle

    [McpServerTool(Name = "list_devices", ReadOnly = true), Description("Lists adb devices/emulators (serial, state, model). Pick a serial for launch_app.")]
    public async Task<string> ListDevices(CancellationToken ct)
    {
        var devices = await new DebugSession().ListDevicesAsync(ct);
        if (devices.Count == 0) return "No adb devices attached.";
        var sb = new StringBuilder();
        foreach (var d in devices)
            sb.AppendLine($"{d.Serial}  state={d.State}  model={d.Model ?? "?"}{(d.IsEmulator ? "  (emulator)" : "")}");
        return sb.ToString();
    }

    [McpServerTool(Name = "launch_app"), Description(
        "Starts (optionally deploys) a .NET Android app with the Mono debugger agent enabled and attaches to it. " +
        "Any previous session is closed. The app is restarted: there is no attach to an already-running process on Mono. " +
        "Helper processes of the package are attached automatically as they start.")]
    public async Task<string> LaunchApp(
        [Description("adb serial of the target device (from list_devices). Mandatory.")] string deviceSerial,
        [Description("Android package name (ApplicationId), e.g. com.company.app")] string packageName,
        [Description("Path to the Android .csproj; required only when deploy=true")] string? projectPath = null,
        [Description("Run `dotnet build -t:Install` for projectPath before launching")] bool deploy = false,
        [Description("Launcher activity as pkg/fully.qualified.Name; resolved automatically when omitted")] string? activityName = null,
        [Description("First SDB port; each extra process gets the next one")] int basePort = 10000,
        [Description("msbuild Configuration for deploy")] string configuration = "Debug",
        CancellationToken ct = default)
    {
        var session = await host.ForLaunchAsync(ct);
        var app = new AppTarget(packageName, activityName, projectPath);
        var options = new LaunchOptions(deviceSerial, basePort, deploy, configuration);
        await session.LaunchAsync(app, options, ct);
        return "Launched and attached.\n" + TextFormat.Status(session.GetStatus());
    }

    [McpServerTool(Name = "attach_to_app"), Description(
        "Alias of launch_app without deploy: on Mono Android attaching means restarting the app with the debugger agent enabled.")]
    public Task<string> AttachToApp(
        [Description("adb serial of the target device")] string deviceSerial,
        [Description("Android package name")] string packageName,
        [Description("Launcher activity pkg/Name; resolved automatically when omitted")] string? activityName = null,
        [Description("First SDB port")] int basePort = 10000,
        CancellationToken ct = default)
        => LaunchApp(deviceSerial, packageName, null, false, activityName, basePort, "Debug", ct);

    [McpServerTool(Name = "get_debug_session_status", ReadOnly = true), Description("Session state, stop generation, last stop, attached processes.")]
    public string GetStatus()
    {
        var s = host.Current;
        return s is null ? "No session." : TextFormat.Status(s.GetStatus());
    }

    [McpServerTool(Name = "terminate_app"), Description("Terminates the app and ends the session (clears the device-side debug property).")]
    public async Task<string> TerminateApp(CancellationToken ct)
    {
        var s = host.Current;
        if (s is null) return "No session.";
        await s.TerminateAsync(ct);
        await host.CloseAsync(ct);
        return "Terminated.";
    }

    [McpServerTool(Name = "detach_debugger"), Description("Ends the session. On Mono Android this also terminates the app (the runtime exits when the debugger disconnects).")]
    public Task<string> Detach(CancellationToken ct) => TerminateApp(ct);

    [McpServerTool(Name = "stop_debugging"), Description("Same as terminate_app.")]
    public Task<string> StopDebugging(CancellationToken ct) => TerminateApp(ct);

    // ------------------------------------------------------------------ execution

    [McpServerTool(Name = "continue_and_wait"), Description("Resumes all stopped processes and waits for the next stop (breakpoint, step, exception, pause) or exit. Returns the stop or 'timeout'.")]
    public async Task<string> ContinueAndWait([Description("Seconds to wait (default 30)")] int? timeoutSeconds = null, CancellationToken ct = default)
    {
        var s = host.Require();
        var stop = await s.ContinueAndWaitAsync(Secs(timeoutSeconds, 30), ct);
        return TextFormat.StopOrTimeout(s, stop);
    }

    [McpServerTool(Name = "wait_until_stopped"), Description("Without afterGeneration: returns the current stop immediately if the session is stopped, otherwise waits for the next stop. With afterGeneration: waits until the stop generation exceeds it. Does not resume anything.")]
    public async Task<string> WaitUntilStopped(
        [Description("Return as soon as the stop generation exceeds this value (use the generation of the last stop you handled)")] long? afterGeneration = null,
        [Description("Seconds to wait (default 30)")] int? timeoutSeconds = null,
        CancellationToken ct = default)
    {
        var s = host.Require();
        var timeout = Secs(timeoutSeconds, 30);
        var stop = afterGeneration is null
            ? await s.WaitForCurrentOrNextStopAsync(timeout, ct)
            : await s.WaitForStopAsync(afterGeneration.Value, timeout, ct);
        return TextFormat.StopOrTimeout(s, stop);
    }

    [McpServerTool(Name = "pause_execution"), Description("Suspends all running processes and reports where they stopped.")]
    public async Task<string> Pause([Description("Seconds to wait (default 10)")] int? timeoutSeconds = null, CancellationToken ct = default)
    {
        var s = host.Require();
        var stop = await s.PauseAsync(Secs(timeoutSeconds, 10), ct);
        return TextFormat.StopOrTimeout(s, stop);
    }

    [McpServerTool(Name = "step_over"), Description("Steps over the current line on a thread (defaults: last stopped pid/thread) and waits for the step to complete.")]
    public Task<string> StepOver([Description("Process id")] int? pid = null, [Description("Thread id")] long? threadId = null, [Description("Seconds to wait (default 20)")] int? timeoutSeconds = null, CancellationToken ct = default)
        => StepCore(pid, threadId, timeoutSeconds, ct, (s, p, t, to) => s.StepOverAsync(p, t, to, ct));

    [McpServerTool(Name = "step_into"), Description("Steps into the call on the current line (defaults: last stopped pid/thread).")]
    public Task<string> StepInto([Description("Process id")] int? pid = null, [Description("Thread id")] long? threadId = null, [Description("Seconds to wait (default 20)")] int? timeoutSeconds = null, CancellationToken ct = default)
        => StepCore(pid, threadId, timeoutSeconds, ct, (s, p, t, to) => s.StepIntoAsync(p, t, to, ct));

    [McpServerTool(Name = "step_out"), Description("Runs until the current method returns (defaults: last stopped pid/thread).")]
    public Task<string> StepOut([Description("Process id")] int? pid = null, [Description("Thread id")] long? threadId = null, [Description("Seconds to wait (default 20)")] int? timeoutSeconds = null, CancellationToken ct = default)
        => StepCore(pid, threadId, timeoutSeconds, ct, (s, p, t, to) => s.StepOutAsync(p, t, to, ct));

    private async Task<string> StepCore(int? pid, long? threadId, int? timeoutSeconds, CancellationToken ct, Func<DebugSession, int, long, TimeSpan, Task<StopEvent?>> step)
    {
        var s = host.Require();
        var (p, t) = host.ResolveTarget(s, pid, threadId);
        var stop = await step(s, p, t, Secs(timeoutSeconds, 20));
        return TextFormat.StopOrTimeout(s, stop);
    }

    // ------------------------------------------------------------------ breakpoints

    [McpServerTool(Name = "set_breakpoint"), Description("Adds a source-line breakpoint. The file path must match the path compiled into the app's PDB (usually the absolute path of the source on this machine).")]
    public string SetBreakpoint(
        [Description("Absolute source file path")] string file,
        [Description("1-based line")] int line,
        [Description("Optional C# condition expression")] string? condition = null,
        [Description("Stop only when hit count >= this value (0 = every hit)")] int hitCount = 0)
    {
        var s = host.RequireForSetup();
        return TextFormat.Breakpoint(s.SetBreakpoint(new BreakpointSpec(file, line, condition, hitCount)));
    }

    [McpServerTool(Name = "set_breakpoints"), Description("Replaces all breakpoints of one file with the given lines (DAP semantics).")]
    public string SetBreakpoints([Description("Absolute source file path")] string file, [Description("1-based lines")] int[] lines)
    {
        var s = host.RequireForSetup();
        var infos = s.SetBreakpoints(file, lines.Select(l => new BreakpointSpec(file, l)).ToList());
        return string.Join('\n', infos.Select(TextFormat.Breakpoint));
    }

    [McpServerTool(Name = "list_breakpoints", ReadOnly = true), Description("Lists breakpoints with ids and verification state.")]
    public string ListBreakpoints()
    {
        var s = host.RequireForSetup();
        var bps = s.ListBreakpoints();
        return bps.Count == 0 ? "No breakpoints." : string.Join('\n', bps.Select(TextFormat.Breakpoint));
    }

    [McpServerTool(Name = "remove_breakpoint"), Description("Removes one breakpoint by id.")]
    public string RemoveBreakpoint([Description("Breakpoint id from set_breakpoint/list_breakpoints")] int id)
        => host.RequireForSetup().RemoveBreakpoint(id) ? $"Removed breakpoint {id}." : $"No breakpoint {id}.";

    [McpServerTool(Name = "remove_all_breakpoints"), Description("Removes all source-line breakpoints (exception filters are kept).")]
    public string RemoveAllBreakpoints()
    {
        host.RequireForSetup().RemoveAllBreakpoints();
        return "All breakpoints removed.";
    }

    [McpServerTool(Name = "set_exception_filters"), Description("Exception types (fully qualified, subclasses included) to stop on when thrown. Unhandled exceptions always stop.")]
    public string SetExceptionFilters([Description("e.g. [\"System.InvalidOperationException\"]; empty clears")] string[] firstChanceTypes)
    {
        host.RequireForSetup().SetExceptionFilters(new ExceptionFilters(true, firstChanceTypes));
        return firstChanceTypes.Length == 0 ? "First-chance filters cleared." : "Stopping on: " + string.Join(", ", firstChanceTypes);
    }

    // ------------------------------------------------------------------ inspection

    [McpServerTool(Name = "get_threads", ReadOnly = true), Description("Threads of the stopped process(es): pid, thread id, name, location.")]
    public string GetThreads([Description("Restrict to one process")] int? pid = null)
    {
        var s = host.Require();
        var threads = s.GetThreads(pid);
        if (threads.Count == 0) return "No threads (is a process stopped?).";
        return string.Join('\n', threads.Select(t => $"pid {t.Pid} thread {t.Id} '{t.Name}' {(string.IsNullOrEmpty(t.Location) ? "" : "@ " + t.Location)}"));
    }

    [McpServerTool(Name = "get_call_stack", ReadOnly = true), Description("Call stack of a thread (defaults: last stopped pid/thread).")]
    public string GetCallStack([Description("Process id")] int? pid = null, [Description("Thread id")] long? threadId = null, [Description("Max frames (default 50)")] int maxFrames = 50)
    {
        var s = host.Require();
        var (p, t) = host.ResolveTarget(s, pid, threadId);
        return TextFormat.Frames(s.GetCallStack(p, t, maxFrames));
    }

    [McpServerTool(Name = "get_locals", ReadOnly = true), Description("Locals (and `this`) of a frame (defaults: last stopped pid/thread, frame 0).")]
    public string GetLocals([Description("Process id")] int? pid = null, [Description("Thread id")] long? threadId = null, [Description("Frame index")] int frameIndex = 0)
    {
        var s = host.Require();
        var (p, t) = host.ResolveTarget(s, pid, threadId);
        return TextFormat.Variables(s.GetLocals(p, t, frameIndex));
    }

    [McpServerTool(Name = "get_variable", ReadOnly = true), Description("One local by name (defaults: last stopped pid/thread, frame 0).")]
    public string GetVariable([Description("Local name")] string name, [Description("Process id")] int? pid = null, [Description("Thread id")] long? threadId = null, [Description("Frame index")] int frameIndex = 0)
    {
        var s = host.Require();
        var (p, t) = host.ResolveTarget(s, pid, threadId);
        var v = s.GetVariable(p, t, frameIndex, name);
        return v is null ? $"No local '{name}'." : TextFormat.Variable(v);
    }

    [McpServerTool(Name = "expand_variable", ReadOnly = true), Description("Children (fields, properties, elements) of a value via its expansion handle from get_locals/evaluate_expression/expand_variable.")]
    public string ExpandVariable([Description("Expansion handle, e.g. 1234:7")] string handle, [Description("Max children (default 100)")] int maxChildren = 100)
        => TextFormat.Variables(host.Require().ExpandVariable(handle, maxChildren));

    [McpServerTool(Name = "evaluate_expression"), Description("Evaluates a C# expression in a frame (defaults: last stopped pid/thread, frame 0). May invoke code in the debuggee.")]
    public string Evaluate([Description("C# expression")] string expression, [Description("Process id")] int? pid = null, [Description("Thread id")] long? threadId = null, [Description("Frame index")] int frameIndex = 0)
    {
        var s = host.Require();
        var (p, t) = host.ResolveTarget(s, pid, threadId);
        return TextFormat.Variable(s.Evaluate(p, t, frameIndex, expression));
    }

    [McpServerTool(Name = "get_current_source_location", ReadOnly = true), Description("Source location of the top frame (defaults: last stopped pid/thread).")]
    public string GetCurrentSourceLocation([Description("Process id")] int? pid = null, [Description("Thread id")] long? threadId = null)
    {
        var s = host.Require();
        var (p, t) = host.ResolveTarget(s, pid, threadId);
        var loc = s.GetCurrentSourceLocation(p, t);
        return loc is null ? "No source location." : TextFormat.Location(loc);
    }

    [McpServerTool(Name = "get_loaded_assemblies", ReadOnly = true), Description("Assemblies loaded in the attached process(es).")]
    public string GetLoadedAssemblies([Description("Restrict to one process")] int? pid = null)
    {
        var list = host.Require().GetLoadedAssemblies(pid);
        return list.Count == 0 ? "No assemblies reported." : string.Join('\n', list.Select(a => $"pid {a.Pid}  {a.Name}  {a.Path ?? ""}"));
    }

    [McpServerTool(Name = "get_app_output", ReadOnly = true), Description("Recent logcat lines of the app's processes plus stdout/stderr captured by the debugger.")]
    public string GetAppOutput([Description("Max lines (default 200)")] int maxLines = 200)
    {
        var lines = host.Require().GetAppOutput(maxLines);
        return lines.Count == 0 ? "(no output)" : string.Join('\n', lines);
    }

    [McpServerTool(Name = "get_debugger_output", ReadOnly = true), Description("Recent engine/Mono.Debugging log lines (attach dance, port rotation, resolution of breakpoints, errors).")]
    public string GetDebuggerOutput([Description("Max lines (default 200)")] int maxLines = 200)
    {
        var s = host.Current;
        if (s is null) return "No session.";
        var lines = s.GetDebuggerOutput(maxLines);
        return lines.Count == 0 ? "(no output)" : string.Join('\n', lines);
    }

    [McpServerTool(Name = "get_exception_details", ReadOnly = true), Description("Type, message and stack trace of the exception of the last stop, if it was an exception stop.")]
    public string GetExceptionDetails()
    {
        var d = host.Require().GetExceptionDetails();
        return d is null ? "Last stop was not an exception stop." : $"{d.Value.Type}: {d.Value.Message}\n{d.Value.StackTrace}";
    }

    [McpServerTool(Name = "get_compact_debug_snapshot", ReadOnly = true), Description("One-call overview at a stop: status, location, call stack (top frames) and locals of the stopped thread.")]
    public string GetCompactSnapshot([Description("Max frames (default 12)")] int maxFrames = 12, [Description("Max locals (default 30)")] int maxLocals = 30)
    {
        var s = host.Require();
        var sb = new StringBuilder();
        sb.AppendLine(TextFormat.Status(s.GetStatus()));
        var last = s.LastStop;
        if (last is null || s.State != SessionState.Stopped) return sb.ToString();
        sb.AppendLine("-- call stack");
        try { sb.AppendLine(TextFormat.Frames(s.GetCallStack(last.Pid, last.ThreadId, maxFrames))); }
        catch (Exception ex) { sb.AppendLine($"(unavailable: {ex.Message})"); }
        sb.AppendLine("-- locals");
        try { sb.AppendLine(TextFormat.Variables(s.GetLocals(last.Pid, last.ThreadId).Take(maxLocals).ToList())); }
        catch (Exception ex) { sb.AppendLine($"(unavailable: {ex.Message})"); }
        if (last.Reason is StopReason.Exception or StopReason.UnhandledException)
        {
            sb.AppendLine("-- exception");
            sb.AppendLine(GetExceptionDetails());
        }
        return sb.ToString();
    }
}
