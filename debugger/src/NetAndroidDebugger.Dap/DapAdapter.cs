using System.Text.Json.Nodes;
using NetAndroidDebugger.Core;

namespace NetAndroidDebugger.Dap;

/// <summary>
/// Debug Adapter Protocol frontend over the same <see cref="DebugSession"/> the MCP server uses.
/// Translation only: no debugging logic lives here.
/// </summary>
public sealed class DapAdapter : IAsyncDisposable
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan BindSettleTime = TimeSpan.FromMilliseconds(750);

    private readonly DapConnection _conn;
    private readonly DapIds _ids = new();
    private readonly List<string> _log = new();
    private readonly DebugSession _session;
    private int _terminated;

    public DapAdapter(DapConnection conn)
    {
        _conn = conn;
        _session = new DebugSession(Log);
        _session.Stopped += OnStopped;
        _session.StateChanged += OnStateChanged;
        _session.AppOutput += OnAppOutput;
    }

    /// <summary>Reads and serves requests until the client disconnects.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            JsonObject? message;
            try { message = await _conn.ReadAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (message is null) break;
            if (message["type"]?.GetValue<string>() != "request") continue;

            var command = message["command"]?.GetValue<string>() ?? "";
            try
            {
                await DispatchAsync(command, message, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Every failure is answered: a DAP client waits forever for a missing response.
                await _conn.SendErrorAsync(message, ex.Message, ct).ConfigureAwait(false);
            }
            if (command is "disconnect" or "terminate") break;
        }
    }

    private async Task DispatchAsync(string command, JsonObject request, CancellationToken ct)
    {
        switch (command)
        {
            case "initialize": await InitializeAsync(request, ct); break;
            case "launch": await LaunchAsync(request, attach: false, ct); break;
            case "attach": await LaunchAsync(request, attach: true, ct); break;
            case "configurationDone": await _conn.SendResponseAsync(request, null, ct); break;
            case "setBreakpoints": await SetBreakpointsAsync(request, ct); break;
            case "setExceptionBreakpoints": await SetExceptionBreakpointsAsync(request, ct); break;
            case "threads": await ThreadsAsync(request, ct); break;
            case "stackTrace": await StackTraceAsync(request, ct); break;
            case "scopes": await ScopesAsync(request, ct); break;
            case "variables": await VariablesAsync(request, ct); break;
            case "evaluate": await EvaluateAsync(request, ct); break;
            case "continue": await ContinueAsync(request, ct); break;
            case "next": await StepAsync(request, StepKind.Over, ct); break;
            case "stepIn": await StepAsync(request, StepKind.Into, ct); break;
            case "stepOut": await StepAsync(request, StepKind.Out, ct); break;
            case "pause": await PauseAsync(request, ct); break;
            case "exceptionInfo": await ExceptionInfoAsync(request, ct); break;
            case "disconnect":
            case "terminate": await DisconnectAsync(request, ct); break;
            default: await _conn.SendErrorAsync(request, "unsupported request '" + command + "'", ct); break;
        }
    }

    // ------------------------------------------------------------------ lifecycle

    private async Task InitializeAsync(JsonObject request, CancellationToken ct)
    {
        var capabilities = new JsonObject
        {
            ["supportsConfigurationDoneRequest"] = true,
            ["supportsEvaluateForHovers"] = true,
            ["supportsConditionalBreakpoints"] = true,
            ["supportsHitConditionalBreakpoints"] = true,
            ["supportsExceptionInfoRequest"] = true,
            ["supportsTerminateRequest"] = true,
            ["exceptionBreakpointFilters"] = new JsonArray(
                new JsonObject { ["filter"] = "uncaught", ["label"] = "Unhandled exceptions", ["default"] = true },
                new JsonObject { ["filter"] = "all", ["label"] = "All exceptions (first chance)", ["default"] = false }),
        };
        await _conn.SendResponseAsync(request, capabilities, ct);
        await _conn.SendEventAsync("initialized", null, ct);
    }

    private async Task LaunchAsync(JsonObject request, bool attach, CancellationToken ct)
    {
        var args = request["arguments"] as JsonObject ?? new JsonObject();
        var serial = Str(args, "deviceSerial") ?? throw new ArgumentException("'deviceSerial' is required (see `adb devices`)");
        var package = Str(args, "packageName") ?? throw new ArgumentException("'packageName' is required (the app's ApplicationId)");

        // Attaching on Mono Android means restarting the app with the agent enabled; the only
        // difference from launching is that deploying makes no sense.
        var deploy = !attach && (Bool(args, "deploy") ?? false);
        var lifetime = Int(args, "propertyLifetimeSeconds");
        var options = new LaunchOptions(
            serial,
            BaseSdbPort: Int(args, "basePort") ?? 10000,
            Deploy: deploy,
            Configuration: Str(args, "configuration") ?? "Debug",
            PropertyLifetime: lifetime is > 0 ? TimeSpan.FromSeconds(lifetime.Value) : null,
            KeepPropertyFresh: Bool(args, "keepPropertyFresh") ?? false);

        var app = new AppTarget(package, Str(args, "activityName"), Str(args, "projectPath"));
        await _session.LaunchAsync(app, options, ct);
        await _conn.SendResponseAsync(request, null, ct);
    }

    private async Task DisconnectAsync(JsonObject request, CancellationToken ct)
    {
        // Answer first, tear down after. Terminating means stopping the logcat reader, clearing the
        // device property, force-stopping the package and removing every forward - seconds on a
        // healthy device, and unbounded on a sick one. A DAP client that is still waiting for this
        // response has no way to tell "slow" from "hung", so it must not have to wait for any of it.
        await _conn.SendResponseAsync(request, null, ct);

        // Detach == terminate on Mono Android: the runtime exits when the debugger disconnects, so
        // honouring `terminateDebuggee: false` is not possible, and pretending otherwise would
        // leave the client believing the app is still running.
        try { await _session.TerminateAsync(CancellationToken.None); }
        catch (Exception ex) { Log("terminating on disconnect: " + ex.Message); }
        await RaiseTerminatedAsync(CancellationToken.None);
    }

    // ------------------------------------------------------------------ breakpoints

    private async Task SetBreakpointsAsync(JsonObject request, CancellationToken ct)
    {
        var args = request["arguments"] as JsonObject ?? new JsonObject();
        var path = args["source"] is JsonObject src ? Str(src, "path") : null;
        if (path is null) throw new ArgumentException("'source.path' is required");

        var specs = new List<BreakpointSpec>();
        foreach (var node in args["breakpoints"] as JsonArray ?? new JsonArray())
        {
            if (node is not JsonObject bp) continue;
            var line = Int(bp, "line") ?? throw new ArgumentException("a breakpoint without a line");
            specs.Add(new BreakpointSpec(path, line, Str(bp, "condition"), HitCountOf(Str(bp, "hitCondition"))));
        }

        var infos = await _session.SetBreakpointsAsync(path, specs, BindSettleTime, ct);
        var body = new JsonObject
        {
            ["breakpoints"] = new JsonArray(infos.Select(i => (JsonNode)new JsonObject
            {
                ["id"] = i.Id,
                ["verified"] = i.Verified,
                ["line"] = i.Spec.Line,
                ["message"] = i.Message,
                ["source"] = new JsonObject { ["path"] = i.Spec.File },
            }).ToArray()),
        };
        await _conn.SendResponseAsync(request, body, ct);
    }

    /// <summary>DAP's hitCondition is free text; the engine only knows "stop from the Nth hit".</summary>
    private static int HitCountOf(string? hitCondition)
        => int.TryParse(hitCondition?.TrimStart('>', '=', ' '), out var n) && n > 0 ? n : 0;

    private async Task SetExceptionBreakpointsAsync(JsonObject request, CancellationToken ct)
    {
        var args = request["arguments"] as JsonObject ?? new JsonObject();
        var filters = (args["filters"] as JsonArray ?? new JsonArray())
            .Select(f => f?.GetValue<string>())
            .Where(f => f is not null)
            .ToHashSet();

        // "all" means every managed exception; the engine takes type names, and System.Exception
        // with subclasses included is exactly that.
        var firstChance = filters.Contains("all") ? new[] { "System.Exception" } : Array.Empty<string>();
        _session.SetExceptionFilters(new ExceptionFilters(BreakOnUnhandled: true, firstChance));
        await _conn.SendResponseAsync(request, null, ct);
    }

    // ------------------------------------------------------------------ state

    private async Task ThreadsAsync(JsonObject request, CancellationToken ct)
    {
        var threads = new JsonArray();
        foreach (var t in _session.GetThreads())
        {
            // One DAP thread list covers every process, so the name has to say which process.
            threads.Add(new JsonObject
            {
                ["id"] = _ids.ThreadId(t.Pid, t.Id),
                ["name"] = t.Name + " (pid " + t.Pid + ")",
            });
        }
        await _conn.SendResponseAsync(request, new JsonObject { ["threads"] = threads }, ct);
    }

    private async Task StackTraceAsync(JsonObject request, CancellationToken ct)
    {
        var args = request["arguments"] as JsonObject ?? new JsonObject();
        var (pid, threadId) = ResolveThread(args);
        var levels = Int(args, "levels") ?? 0;
        var frames = _session.GetCallStack(pid, threadId, levels > 0 ? levels : 50);

        var body = new JsonArray();
        foreach (var f in frames)
        {
            var frame = new JsonObject
            {
                ["id"] = _ids.FrameId(pid, threadId, f.Index),
                ["name"] = f.Method,
                ["line"] = f.Line,
                ["column"] = f.Column,
            };
            if (!string.IsNullOrEmpty(f.File))
                frame["source"] = new JsonObject { ["path"] = f.File, ["name"] = Path.GetFileName(f.File) };
            else
                // No source to open: let the client grey the frame out instead of hunting for a file.
                frame["presentationHint"] = "subtle";
            body.Add(frame);
        }
        await _conn.SendResponseAsync(request, new JsonObject { ["stackFrames"] = body, ["totalFrames"] = frames.Count }, ct);
    }

    private async Task ScopesAsync(JsonObject request, CancellationToken ct)
    {
        var args = request["arguments"] as JsonObject ?? new JsonObject();
        var frameId = Int(args, "frameId") ?? throw new ArgumentException("'frameId' is required");
        if (!_ids.TryFrame(frameId, out var frame))
            throw new ArgumentException("frame " + frameId + " is not valid any more (the process resumed)");

        var reference = _ids.VariableId(new DapIds.VariableRef(frame.Pid, frame.ThreadId, frame.Frame, null));
        var scopes = new JsonArray(new JsonObject
        {
            ["name"] = "Locals",
            ["variablesReference"] = reference,
            ["expensive"] = false,
        });
        await _conn.SendResponseAsync(request, new JsonObject { ["scopes"] = scopes }, ct);
    }

    private async Task VariablesAsync(JsonObject request, CancellationToken ct)
    {
        var args = request["arguments"] as JsonObject ?? new JsonObject();
        var reference = Int(args, "variablesReference") ?? throw new ArgumentException("'variablesReference' is required");
        if (!_ids.TryVariable(reference, out var target))
            throw new ArgumentException("variablesReference " + reference + " is not valid any more (the process resumed)");

        var values = target.ExpansionHandle is null
            ? _session.GetLocals(target.Pid, target.ThreadId, target.Frame)
            : _session.ExpandVariable(target.ExpansionHandle);

        var body = new JsonArray();
        foreach (var v in values) body.Add(ToVariable(v, target));
        await _conn.SendResponseAsync(request, new JsonObject { ["variables"] = body }, ct);
    }

    private JsonObject ToVariable(VariableSnapshot v, DapIds.VariableRef owner)
    {
        var child = v.ExpansionHandle is null
            ? 0
            : _ids.VariableId(new DapIds.VariableRef(owner.Pid, owner.ThreadId, owner.Frame, v.ExpansionHandle));
        return new JsonObject
        {
            ["name"] = v.Name,
            ["value"] = v.DisplayValue,
            ["type"] = v.TypeName,
            ["variablesReference"] = child,
        };
    }

    private async Task EvaluateAsync(JsonObject request, CancellationToken ct)
    {
        var args = request["arguments"] as JsonObject ?? new JsonObject();
        var expression = Str(args, "expression") ?? throw new ArgumentException("'expression' is required");
        var frameId = Int(args, "frameId");

        int pid;
        long threadId;
        var frameIndex = 0;
        if (frameId is not null && _ids.TryFrame(frameId.Value, out var frame))
        {
            pid = frame.Pid;
            threadId = frame.ThreadId;
            frameIndex = frame.Frame;
        }
        else
        {
            var last = _session.LastStop ?? throw new InvalidOperationException("nothing is stopped, so there is no frame to evaluate in");
            pid = last.Pid;
            threadId = last.ThreadId;
        }

        var result = _session.Evaluate(pid, threadId, frameIndex, expression);
        if (result.IsError) throw new InvalidOperationException(result.Value);

        var reference = result.ExpansionHandle is null
            ? 0
            : _ids.VariableId(new DapIds.VariableRef(pid, threadId, frameIndex, result.ExpansionHandle));
        var body = new JsonObject
        {
            ["result"] = result.DisplayValue,
            ["type"] = result.TypeName,
            ["variablesReference"] = reference,
        };
        await _conn.SendResponseAsync(request, body, ct);
    }

    private async Task ExceptionInfoAsync(JsonObject request, CancellationToken ct)
    {
        var details = _session.GetExceptionDetails() ?? throw new InvalidOperationException("the last stop was not an exception");
        var (type, message, stack) = details;
        var body = new JsonObject
        {
            ["exceptionId"] = type,
            ["description"] = message,
            ["breakMode"] = "always",
            ["details"] = new JsonObject
            {
                ["message"] = message,
                ["typeName"] = type,
                ["stackTrace"] = stack,
            },
        };
        await _conn.SendResponseAsync(request, body, ct);
    }

    // ------------------------------------------------------------------ execution

    private enum StepKind { Over, Into, Out }

    private async Task ContinueAsync(JsonObject request, CancellationToken ct)
    {
        _session.Continue();
        await _conn.SendResponseAsync(request, new JsonObject { ["allThreadsContinued"] = true }, ct);
    }

    private async Task StepAsync(JsonObject request, StepKind kind, CancellationToken ct)
    {
        var args = request["arguments"] as JsonObject ?? new JsonObject();
        var (pid, threadId) = ResolveThread(args);
        // The acknowledgement goes out first: DAP expects the stopped event to follow it.
        await _conn.SendResponseAsync(request, null, ct);
        var step = kind switch
        {
            StepKind.Over => _session.StepOverAsync(pid, threadId, StepTimeout, ct),
            StepKind.Into => _session.StepIntoAsync(pid, threadId, StepTimeout, ct),
            _ => _session.StepOutAsync(pid, threadId, StepTimeout, ct),
        };
        await step.ConfigureAwait(false);
    }

    private async Task PauseAsync(JsonObject request, CancellationToken ct)
    {
        await _conn.SendResponseAsync(request, null, ct);
        await _session.PauseAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ events

    private void OnStopped(object? sender, StopEvent e)
    {
        _ids.InvalidateStopScoped();
        var body = new JsonObject
        {
            ["reason"] = e.Reason switch
            {
                StopReason.Breakpoint => "breakpoint",
                StopReason.Step => "step",
                StopReason.Pause => "pause",
                _ => "exception",
            },
            ["threadId"] = _ids.ThreadId(e.Pid, e.ThreadId),
            ["allThreadsStopped"] = true,
        };
        if (e.Reason is StopReason.Exception or StopReason.UnhandledException)
            body["text"] = string.IsNullOrEmpty(e.ExceptionType) ? e.Message : e.ExceptionType + ": " + e.Message;
        Fire("stopped", body);
    }

    private void OnStateChanged(object? sender, SessionState state)
    {
        if (state == SessionState.Running) Fire("continued", new JsonObject { ["allThreadsContinued"] = true });
        else if (state == SessionState.Exited) _ = RaiseTerminatedAsync(CancellationToken.None);
    }

    private void OnAppOutput(object? sender, AppLogLine line)
        => Fire("output", new JsonObject
        {
            ["category"] = line.Level is 'E' or 'F' ? "stderr" : "stdout",
            ["output"] = line.ToString() + "\n",
        });

    private async Task RaiseTerminatedAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _terminated, 1) != 0) return;
        try
        {
            await _conn.SendEventAsync("terminated", null, ct).ConfigureAwait(false);
            await _conn.SendEventAsync("exited", new JsonObject { ["exitCode"] = 0 }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { Log("sending terminated: " + ex.Message); }
    }

    /// <summary>Events come from engine threads; a send must never break the engine.</summary>
    private void Fire(string @event, JsonObject body)
        => _ = Task.Run(async () =>
        {
            try { await _conn.SendEventAsync(@event, body, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { Log("sending " + @event + ": " + ex.Message); }
        });

    // ------------------------------------------------------------------ helpers

    private (int Pid, long ThreadId) ResolveThread(JsonObject args)
    {
        var threadId = Int(args, "threadId");
        if (threadId is not null && _ids.TryThread(threadId.Value, out var target)) return target;
        var last = _session.LastStop ?? throw new InvalidOperationException("nothing is stopped and no usable threadId was given");
        return (last.Pid, last.ThreadId);
    }

    private void Log(string line)
    {
        lock (_log)
        {
            _log.Add(line);
            if (_log.Count > 500) _log.RemoveRange(0, _log.Count - 500);
        }
    }

    private static string? Str(JsonObject o, string name)
        => o[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static int? Int(JsonObject o, string name)
        => o[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static bool? Bool(JsonObject o, string name)
        => o[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    public async ValueTask DisposeAsync() => await _session.DisposeAsync();
}
