using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using NetAndroidDebugger.Core;
using NetAndroidDebugger.Core.Launch;
using NetAndroidDebugger.Frontends;


namespace NetAndroidDebugger.Mcp;

/// <summary>
/// MCP tool surface. Thin translation layer: every tool maps to one or two
/// <see cref="DebugSession"/> calls and renders plain text for the model.
/// </summary>
[McpServerToolType]
public sealed class DebuggerTools(SessionHost host)
{
    private static TimeSpan Secs(int? s, int dflt) => TimeSpan.FromSeconds(s is > 0 ? s.Value : dflt);

    /// <summary>How long a breakpoint call waits for the runtime to bind before answering.</summary>
    private static readonly TimeSpan BindSettleTime = TimeSpan.FromMilliseconds(750);

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

    [McpServerTool(Name = "list_app_projects", ReadOnly = true), Description(
        "Lists the .NET for Android application projects a solution or folder holds, with the ApplicationId each one " +
        "declares: the counterpart of list_devices for choosing what to launch. Libraries are not listed even though " +
        "they target Android too - an application declares an ApplicationId, or is an Exe. Projects named by a solution " +
        "beside them come first, since a project merely sitting in the tree is usually not the one meant.")]
    public string ListAppProjects(
        [Description("Solution (.sln/.slnx), .csproj, or folder to search")] string solutionOrFolder)
    {
        IReadOnlyList<AppProjectInfo> found;
        try { found = AppProjectFinder.Find(solutionOrFolder); }
        catch (LaunchException ex) { throw new McpException(ex.Message); }

        return found.Count == 0
            ? $"No .NET for Android application project under {Path.GetFullPath(solutionOrFolder)}. "
              + "Libraries that target Android are not launchable."
            : string.Join('\n', found.Select(AppProjectFinder.Line));
    }

    [McpServerTool(Name = "launch_app"), Description(
        "Starts (optionally deploys) a .NET Android app with the Mono debugger agent enabled and attaches to it. " +
        "What the project already declares need not be restated: given projectPath or solutionOrFolder, the package " +
        "name is read from the project file, and a single ready device is used as it is. Nothing ambiguous is ever " +
        "guessed - several candidate projects or several ready devices fail listing them. " +
        "Any previous session is closed. The app is restarted: there is no attach to an already-running process on Mono. " +
        "Helper processes of the package are attached automatically as they start.")]
    public async Task<string> LaunchApp(
        [Description("adb serial of the target device (from list_devices). Omit when exactly one device is ready.")] string? deviceSerial = null,
        [Description("Android package name (ApplicationId), e.g. com.company.app. Omit to read it from the project file.")] string? packageName = null,
        [Description("Path to the Android .csproj; required for deploy=true, and enough to deduce packageName")] string? projectPath = null,
        [Description("Run `dotnet build -t:Install` for projectPath before launching")] bool deploy = false,
        [Description("Solution (.sln/.slnx) or folder holding the app project, when neither packageName nor projectPath is given (see list_app_projects)")] string? solutionOrFolder = null,
        [Description("Launcher activity as pkg/fully.qualified.Name; resolved automatically when omitted")] string? activityName = null,
        [Description("First SDB port; each extra process gets the next one")] int basePort = 10000,
        [Description("msbuild Configuration for deploy")] string configuration = "Debug",
        [Description("How long (seconds) the device-side debug property stays valid after launch (default 180). Helper processes of the app that start later than this run without debugger; any OTHER Mono app starting within this window is disturbed (it waits for a debugger on our port), so keep it short.")] int? propertyLifetimeSeconds = null,
        [Description("Keep the debug property valid for the whole session, so processes the app starts much later (on-demand services, crash reporters) are still debugged. Leaves the window open for other Mono apps to pick up our port the entire time.")] bool keepPropertyFresh = false,
        CancellationToken ct = default)
    {
        var deduced = new StringBuilder();

        // The package name is in the .csproj. Copying it into every call is not merely tedious: a
        // copy can contradict the project, and then the debugger launches an app nobody is building.
        if (packageName is null || (deploy && projectPath is null))
        {
            AppProjectInfo project;
            try
            {
                project = projectPath is not null
                    ? AppProjectFinder.Describe(projectPath) ?? throw new McpException(
                        File.Exists(projectPath)
                            ? $"{projectPath} is not a .NET for Android application project: one targets an -android " +
                              "framework and declares an ApplicationId (or is an Exe). It may also simply not be readable XML."
                            : $"{projectPath} does not exist.")
                    : solutionOrFolder is not null
                        ? AppProjectFinder.Single(solutionOrFolder)
                        // Naming the argument that is actually missing: with deploy the caller has usually
                        // given packageName already, and telling them to give it again wastes a round trip.
                        : throw new McpException(packageName is not null
                            ? "deploy needs the project to build: give projectPath, or solutionOrFolder to find it in."
                            : "Nothing to launch. Give packageName, or projectPath, or solutionOrFolder to find the project in "
                              + "(list_app_projects lists them).");
            }
            catch (LaunchException ex) { throw new McpException(ex.Message); }

            projectPath ??= project.ProjectPath;
            if (packageName is null)
            {
                packageName = project.ApplicationId ?? throw new McpException(
                    $"{project.ProjectPath} declares no ApplicationId - it is probably set in a props file. Pass packageName.");
                deduced.AppendLine($"{packageName} from {project.ProjectPath}");
            }
            else if (project.ApplicationId is { Length: > 0 } declared
                     && !string.Equals(declared, packageName, StringComparison.Ordinal))
            {
                // The contradiction this deduction exists to prevent. Fatal when deploying, because
                // installing one app and starting another fails as "no debugger agent within Ns",
                // which sends the reader looking anywhere but here.
                var mismatch = $"packageName '{packageName}' contradicts the ApplicationId '{declared}' declared by {project.ProjectPath}";
                if (deploy) throw new McpException($"{mismatch}. Deploying would install one app and launch the other.");
                deduced.AppendLine($"WARNING: {mismatch}; launching '{packageName}' as asked");
            }
        }

        // Resolved before the session exists, so a device that cannot be chosen leaves nothing behind.
        string serial;
        try { serial = await new DebugSession().ResolveDeviceSerialAsync(deviceSerial, ct); }
        catch (LaunchException ex) { throw new McpException(ex.Message); }
        if (deviceSerial is null) deduced.AppendLine($"device {serial}, the only one ready");

        var session = await host.ForLaunchAsync(ct);
        var app = new AppTarget(packageName, activityName, projectPath);
        var options = new LaunchOptions(serial, basePort, deploy, configuration,
            PropertyLifetime: propertyLifetimeSeconds is > 0 ? TimeSpan.FromSeconds(propertyLifetimeSeconds.Value) : null,
            KeepPropertyFresh: keepPropertyFresh);
        await session.LaunchAsync(app, options, ct);

        return "Launched and attached.\n"
             + (deduced.Length > 0 ? "Deduced: " + string.Join("; ", DeducedLines(deduced)) + "\n" : "")
             + TextFormat.Status(session.GetStatus());
    }

    /// <summary>What was deduced rather than given, one item per line.</summary>
    private static IEnumerable<string> DeducedLines(StringBuilder deduced) =>
        deduced.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [McpServerTool(Name = "attach_to_app"), Description(
        "Alias of launch_app without deploy: on Mono Android attaching means restarting the app with the debugger agent enabled.")]
    public Task<string> AttachToApp(
        [Description("adb serial of the target device; omit when exactly one device is ready")] string? deviceSerial = null,
        [Description("Android package name; omit to read it from the project file")] string? packageName = null,
        [Description("Launcher activity pkg/Name; resolved automatically when omitted")] string? activityName = null,
        [Description("First SDB port")] int basePort = 10000,
        [Description("Solution or folder holding the app project, when packageName is not given")] string? solutionOrFolder = null,
        CancellationToken ct = default)
        => LaunchApp(deviceSerial, packageName, projectPath: null, deploy: false, solutionOrFolder: solutionOrFolder,
                     activityName: activityName, basePort: basePort, ct: ct);

    [McpServerTool(Name = "launch_from_config"), Description(
        "Launches from a VS Code launch.json - the same file, and the same field names, the DAP frontend reads - so " +
        "how an app is launched lives with the project instead of being restated on every call. Takes the first " +
        "'net-android' configuration unless configName says otherwise; JSONC, ${workspaceFolder} and ${env:VAR} are " +
        "handled, and relative paths resolve against the project. exceptionRules in the configuration are applied to " +
        "the session, so an app's own noisy exceptions travel with its repository.")]
    public async Task<string> LaunchFromConfig(
        [Description("Path to launch.json; defaults to .vscode/launch.json under workspaceFolder")] string? configFile = null,
        [Description("Which configuration, by its \"name\"; defaults to the first net-android one")] string? configName = null,
        [Description("Base for ${workspaceFolder} and relative paths; defaults to the folder holding the file, or its parent when that folder is .vscode")] string? workspaceFolder = null,
        [Description("Overrides the configuration's deviceSerial, for a one-off run on another device")] string? deviceSerial = null,
        [Description("Overrides the configuration's deploy, to relaunch without rebuilding")] bool? deploy = null,
        CancellationToken ct = default)
    {
        LaunchConfig config;
        try
        {
            config = LaunchConfigFile.Read(configFile, configName, workspaceFolder);
        }
        catch (Exception ex) when (ex is FormatException or FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            // Reading the file is the caller's mistake to fix, and the message already names the file
            // and lists what it offers; an MCP error keeps it out of the debugger log.
            throw new McpException(ex.Message);
        }

        var session = await host.ForLaunchAsync(ct);
        var notes = new StringBuilder();

        if (config.Rules.Count > 0)
        {
            session.SetExceptionRules(config.Rules);
            notes.AppendLine($"{config.Rules.Count} exception rule(s) from the configuration.");
        }

        if (config.UseGlobalExceptionRules)
        {
            var shared = new ExceptionRuleFile(config.GlobalExceptionRulesPath ?? ExceptionRuleFile.DefaultPath);
            // A broken shared file must not stop a launch: the session is perfectly usable without it,
            // and this line says where to look.
            try { _ = shared.Load(); session.SetGlobalExceptionRuleSource(shared); }
            catch (Exception ex) { notes.AppendLine($"Ignoring the shared exception rules: {ex.Message}"); }
        }

        var serial = deviceSerial ?? config.DeviceSerial;
        if (serial != config.DeviceSerial)
            notes.AppendLine($"Device {serial} overrides {config.DeviceSerial} from the configuration.");

        var app = new AppTarget(config.PackageName, config.ActivityName, config.ProjectPath);
        var options = new LaunchOptions(
            serial,
            BaseSdbPort: config.BasePort,
            Deploy: deploy ?? config.Deploy,
            Configuration: config.Configuration,
            PropertyLifetime: config.PropertyLifetimeSeconds is > 0 ? TimeSpan.FromSeconds(config.PropertyLifetimeSeconds.Value) : null,
            KeepPropertyFresh: config.KeepPropertyFresh);
        await session.LaunchAsync(app, options, ct);

        return $"Launched '{config.Name}' from {config.Origin}.\n{notes}{TextFormat.Status(session.GetStatus())}";
    }

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
    public async Task<string> ContinueAndWait(
        [Description("Seconds to wait (default 30)")] int? timeoutSeconds = null,
        [Description(SnapshotArgument)] bool snapshot = false,
        CancellationToken ct = default)
    {
        var s = host.Require();
        var stop = await s.ContinueAndWaitAsync(Secs(timeoutSeconds, 30), ct);
        return WithSnapshot(s, TextFormat.StopOrTimeout(s, stop), stop, snapshot);
    }

    /// <summary>
    /// Reading locals means invoking code in the debuggee, and breakpoints are disarmed for the
    /// duration of any evaluation. Paying that on every step, unasked, is not a trade this engine
    /// can make for the caller - hence opt-in, unlike debuggers whose values need no invocation.
    /// </summary>
    private const string SnapshotArgument =
        "Append the call stack and locals of the new stop, saving two round trips. Off by default: reading locals " +
        "calls into the debuggee, which disarms breakpoints for the duration.";

    /// <summary>Appends the snapshot when it was asked for and something actually stopped.</summary>
    private string WithSnapshot(DebugSession s, string text, StopEvent? stop, bool snapshot)
        => snapshot && stop is not null ? text + "\n" + CompactSnapshot(s, 12, 30) : text;

    [McpServerTool(Name = "wait_until_stopped"), Description("Without afterGeneration: returns the current stop immediately if the session is stopped, otherwise waits for the next stop. With afterGeneration: waits until the stop generation exceeds it. Does not resume anything.")]
    public async Task<string> WaitUntilStopped(
        [Description("Return as soon as the stop generation exceeds this value (use the generation of the last stop you handled)")] long? afterGeneration = null,
        [Description("Seconds to wait (default 30)")] int? timeoutSeconds = null,
        [Description(SnapshotArgument)] bool snapshot = false,
        CancellationToken ct = default)
    {
        var s = host.Require();
        var timeout = Secs(timeoutSeconds, 30);
        var stop = afterGeneration is null
            ? await s.WaitForCurrentOrNextStopAsync(timeout, ct)
            : await s.WaitForStopAsync(afterGeneration.Value, timeout, ct);
        return WithSnapshot(s, TextFormat.StopOrTimeout(s, stop), stop, snapshot);
    }

    [McpServerTool(Name = "pause_execution"), Description("Suspends all running processes and reports where they stopped.")]
    public async Task<string> Pause([Description("Seconds to wait (default 10)")] int? timeoutSeconds = null, CancellationToken ct = default)
    {
        var s = host.Require();
        var stop = await s.PauseAsync(Secs(timeoutSeconds, 10), ct);
        return TextFormat.StopOrTimeout(s, stop);
    }

    [McpServerTool(Name = "step_over"), Description("Steps over the current line on a thread (defaults: last stopped pid/thread) and waits for the step to complete.")]
    public Task<string> StepOver([Description("Process id")] int? pid = null, [Description("Thread id")] long? threadId = null, [Description("Seconds to wait (default 20)")] int? timeoutSeconds = null, [Description(SnapshotArgument)] bool snapshot = false, CancellationToken ct = default)
        => StepCore(pid, threadId, timeoutSeconds, snapshot, ct, (s, p, t, to) => s.StepOverAsync(p, t, to, ct));

    [McpServerTool(Name = "step_into"), Description("Steps into the call on the current line (defaults: last stopped pid/thread).")]
    public Task<string> StepInto([Description("Process id")] int? pid = null, [Description("Thread id")] long? threadId = null, [Description("Seconds to wait (default 20)")] int? timeoutSeconds = null, [Description(SnapshotArgument)] bool snapshot = false, CancellationToken ct = default)
        => StepCore(pid, threadId, timeoutSeconds, snapshot, ct, (s, p, t, to) => s.StepIntoAsync(p, t, to, ct));

    [McpServerTool(Name = "step_out"), Description("Runs until the current method returns (defaults: last stopped pid/thread).")]
    public Task<string> StepOut([Description("Process id")] int? pid = null, [Description("Thread id")] long? threadId = null, [Description("Seconds to wait (default 20)")] int? timeoutSeconds = null, [Description(SnapshotArgument)] bool snapshot = false, CancellationToken ct = default)
        => StepCore(pid, threadId, timeoutSeconds, snapshot, ct, (s, p, t, to) => s.StepOutAsync(p, t, to, ct));

    private async Task<string> StepCore(int? pid, long? threadId, int? timeoutSeconds, bool snapshot, CancellationToken ct, Func<DebugSession, int, long, TimeSpan, Task<StopEvent?>> step)
    {
        var s = host.Require();
        var (p, t) = host.ResolveTarget(s, pid, threadId);
        var stop = await step(s, p, t, Secs(timeoutSeconds, 20));
        return WithSnapshot(s, TextFormat.StopOrTimeout(s, stop), stop, snapshot);
    }

    // ------------------------------------------------------------------ breakpoints

    [McpServerTool(Name = "set_breakpoint"), Description(
        "Adds a source-line breakpoint. The file path must match the path compiled into the app's PDB " +
        "(get_source_files reports it). When a session is running, the answer waits briefly for the runtime to bind it. " +
        "With logMessage the app is NOT suspended: the message is written to get_debugger_output instead, which is " +
        "often the only usable form on an app that talks to a backend, since suspending it makes the backend time out.")]
    public async Task<string> SetBreakpoint(
        [Description("Absolute source file path")] string file,
        [Description("1-based line")] int line,
        [Description("Optional C# condition expression")] string? condition = null,
        [Description("Stop only when hit count >= this value (0 = every hit)")] int hitCount = 0,
        [Description("Richer hit count: 5 or >=5 (from the fifth hit), >5 (after it), =5 (only it), %5 (every fifth). Wins over hitCount.")] string? hitCondition = null,
        [Description("Log instead of stopping: the message is traced with each {expression} evaluated in place, and the app keeps running")] string? logMessage = null,
        CancellationToken ct = default)
    {
        var s = host.RequireForSetup();
        var spec = new BreakpointSpec(file, line, condition, hitCount, hitCondition, logMessage);
        var info = await s.SetBreakpointAsync(spec, BindSettleTime, ct);
        return TextFormat.Breakpoint(info);
    }

    [McpServerTool(Name = "set_breakpoints"), Description("Replaces all breakpoints of one file with the given lines (DAP semantics).")]
    public async Task<string> SetBreakpoints(
        [Description("Absolute source file path")] string file,
        [Description("1-based lines")] int[] lines,
        CancellationToken ct = default)
    {
        var s = host.RequireForSetup();
        var infos = await s.SetBreakpointsAsync(file, lines.Select(l => new BreakpointSpec(file, l)).ToList(), BindSettleTime, ct);
        return infos.Count == 0 ? "No breakpoints." : string.Join('\n', infos.Select(TextFormat.Breakpoint));
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

    [McpServerTool(Name = "set_exception_rules"), Description(
        "Per-exception rules, in order; the first whose criteria all match decides what happens. " +
        "Criteria that are set are AND-ed, an unset one matches anything. This is what makes a noisy app workable: " +
        "a real app throws on purpose (network timeouts, handled retries), so 'stop on everything' and 'stop on nothing' " +
        "are both useless. Rules apply to first-chance exceptions; an unhandled one always stops. " +
        "An empty list restores plain filter behaviour. " +
        "Each rule is {action, type?, typeContains?, messageContains?, messageRegex?, sourceFileContains?} " +
        "with action one of break | log | logStack | ignore. " +
        "Example: [{\"type\":\"MQTTnet.Exceptions.MqttCommunicationTimedOutException\",\"action\":\"ignore\"}," +
        "{\"messageContains\":\"connect/disconnect is pending\",\"action\":\"log\"},{\"action\":\"break\"}]")]
    public string SetExceptionRules(
        [Description("JSON array of rules, in order. [] clears them.")] string rules)
    {
        var parsed = ParseExceptionRules(rules);
        host.RequireForSetup().SetExceptionRules(parsed);
        if (parsed.Count == 0) return "No exception rules; the filters decide.";
        return string.Join('\n', parsed.Select((r, i) => $"{i + 1}. {DescribeRule(r)}"));
    }


    [McpServerTool(Name = "use_global_exception_rules"), Description(
        "Points the session at a shared rules file, consulted after the session's own rules. It is re-read on every " +
        "resume when it has changed, so a rule can be edited while the app is stopped and govern what happens next. " +
        "Default path: %USERPROFILE%\\.net-android-debugger\\exceptionRules.json. The file is a JSON array of rules, " +
        "or an object with an 'exceptionRules' array. Call with enabled=false to stop consulting it.")]
    public string UseGlobalExceptionRules(
        [Description("Path to the rules file; omit for the default")] string? path = null,
        [Description("false detaches the shared file")] bool enabled = true)
    {
        var session = host.RequireForSetup();
        if (!enabled)
        {
            session.SetGlobalExceptionRuleSource(null);
            return "The shared rules file is no longer consulted.";
        }

        var file = new ExceptionRuleFile(path ?? ExceptionRuleFile.DefaultPath);
        // Read it once here so a broken file is this call's error, rather than a surprise on the
        // first exception with the app already running.
        IReadOnlyList<ExceptionRule> rules;
        try { rules = file.Load(); }
        catch (Exception ex) { throw new McpException($"{file.Description}: {ex.Message}"); }

        session.SetGlobalExceptionRuleSource(file);
        if (rules.Count == 0)
        {
            return File.Exists(file.Description)
                ? $"{file.Description} holds no rules yet; it is watched and will be re-read when it changes."
                : $"{file.Description} does not exist yet; it is watched and will be read as soon as it does.";
        }
        return $"{file.Description}: {rules.Count} shared rule(s), after the session's own.\n"
             + string.Join('\n', rules.Select((r, i) => $"{i + 1}. {DescribeRule(r)}"));
    }
    [McpServerTool(Name = "get_exception_rules", ReadOnly = true), Description("The exception rules in force, in order.")]
    public string GetExceptionRules()
    {
        var rules = host.RequireForSetup().GetExceptionRules();
        return rules.Count == 0
            ? "No exception rules; the filters decide."
            : string.Join('\n', rules.Select((r, i) => $"{i + 1}. {DescribeRule(r)}"));
    }

    private static string DescribeRule(ExceptionRule r)
    {
        var criteria = new List<string>();
        if (r.Type is { Length: > 0 }) criteria.Add($"type={r.Type}");
        if (r.TypeContains is { Length: > 0 }) criteria.Add($"typeContains={r.TypeContains}");
        if (r.MessageContains is { Length: > 0 }) criteria.Add($"messageContains=\"{r.MessageContains}\"");
        if (r.MessageRegex is { Length: > 0 }) criteria.Add($"messageRegex=/{r.MessageRegex}/");
        if (r.SourceFileContains is { Length: > 0 }) criteria.Add($"sourceFileContains={r.SourceFileContains}");
        var what = criteria.Count == 0 ? "any exception" : string.Join(" and ", criteria);
        return $"{r.Action.ToString().ToLowerInvariant()} on {what}";
    }

    /// <summary>
    /// Parses the rule array. The reader is shared with the DAP frontend and with the rules file,
    /// so one format is described in one place.
    /// </summary>
    private static List<ExceptionRule> ParseExceptionRules(string json)
    {
        try { return ExceptionRuleFile.Parse(json, "rules").ToList(); }
        catch (FormatException ex) { throw new McpException(ex.Message); }
    }


    [McpServerTool(Name = "set_evaluation_options"), Description(
        "Tunes how values are read. Timeouts are per expression and per member; " +
        "on very slow emulators set allowToStringCalls=false (or allowTargetInvoke=false) to avoid wedging the stopped thread " +
        "in a debuggee call. Returns the options in force.")]
    public string SetEvaluationOptions(
        [Description("Per-expression timeout in ms (default 6000)")] int? evaluationTimeoutMs = null,
        [Description("Per-member timeout in ms when expanding objects (default 10000)")] int? memberEvaluationTimeoutMs = null,
        [Description("Call ToString() in the debuggee to render values (default true)")] bool? allowToStringCalls = null,
        [Description("Allow any method/property invocation in the debuggee (default true; false = fields only)")] bool? allowTargetInvoke = null)
    {
        var s = host.RequireForSetup();
        s.SetEvaluationOptions(evaluationTimeoutMs, memberEvaluationTimeoutMs, allowToStringCalls, allowTargetInvoke);
        var o = s.GetEvaluationOptions();
        // Lower case on purpose: these read back the values the caller passes as JSON booleans,
        // and .NET's "True"/"False" would be the only place in the surface spelling them differently.
        return $"evaluationTimeoutMs={o.EvaluationTimeoutMs} memberEvaluationTimeoutMs={o.MemberEvaluationTimeoutMs} "
             + $"allowToStringCalls={(o.AllowToStringCalls ? "true" : "false")} allowTargetInvoke={(o.AllowTargetInvoke ? "true" : "false")}";
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

    [McpServerTool(Name = "get_loaded_assemblies", ReadOnly = true), Description(
        "Assemblies loaded in the attached process(es), each with whether the runtime has debug information for it. " +
        "That is the first thing to check when a breakpoint stays pending: no line in an assembly without symbols can " +
        "ever bind, so comparing source paths against it is wasted effort. \"symbols unknown\" means the debuggee's " +
        "protocol version cannot answer, not that symbols are missing.")]
    public string GetLoadedAssemblies([Description("Restrict to one process")] int? pid = null)
    {
        var list = host.Require().GetLoadedAssemblies(pid);
        return list.Count == 0
            ? "No assemblies reported."
            : string.Join('\n', list.Select(a =>
                $"pid {a.Pid}  {a.Name}  {a.HasSymbols switch { true => "symbols", false => "NO SYMBOLS", null => "symbols unknown" }}  {a.Path ?? ""}"));
    }


    [McpServerTool(Name = "get_source_files", ReadOnly = true), Description(
        "Paths the debuggee's runtime has for a source file, exactly as compiled into the PDB. " +
        "Use it when a breakpoint stays pending: set_breakpoint matches the path you give against these, " +
        "so comparing them says whether the path is wrong or the type has simply not been loaded yet. " +
        "Pass a file name or a full path; only the file name is matched. Works while the app is running.")]
    public string GetSourceFiles(
        [Description("Source file name or path, e.g. MainActivity.cs")] string file,
        [Description("Restrict to one process")] int? pid = null)
    {
        var list = host.Require().GetSourceFiles(file, pid);
        if (list.Count == 0)
            return $"No process reports a source file named '{Path.GetFileName(file)}'. Either no type from it is loaded yet, or the app was built from different sources.";
        return string.Join('\n', list.Select(f => $"pid {f.Pid}  {f.Path}  [{string.Join(", ", f.Types)}]"));
    }
    [McpServerTool(Name = "get_app_output", ReadOnly = true), Description(
        "Recent output of the app's processes: logcat lines plus anything the debuggee wrote to stdout/stderr " +
        "(tags 'stdout'/'stderr'). Rendered as 'HH:mm:ss.fff LEVEL/Tag(pid): message', oldest first. " +
        "Filters are applied before the line limit, so narrowing them surfaces older matches instead of fewer.")]
    public string GetAppOutput(
        [Description("Max lines (default 200)")] int maxLines = 200,
        [Description("Lowest Android priority to include: V, D, I, W, E or F")] string? minLevel = null,
        [Description("Only lines whose tag contains this text")] string? tagContains = null,
        [Description("Only lines whose message contains this text")] string? contains = null,
        [Description("Only lines from this process id")] int? pid = null)
    {
        var lines = host.Require().GetAppOutput(maxLines, string.IsNullOrEmpty(minLevel) ? null : minLevel[0], tagContains, contains, pid);
        return lines.Count == 0 ? "(no output)" : string.Join('\n', lines.Select(l => l.ToString()));
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
        => CompactSnapshot(host.Require(), maxFrames, maxLocals);

    /// <summary>
    /// Status, stack and locals at a stop. Also what <c>snapshot=true</c> appends to a step or a
    /// resume: it costs a round trip saved, and locals cost calls into the debuggee, which is why
    /// nobody pays for it unless they ask.
    /// </summary>
    private string CompactSnapshot(DebugSession s, int maxFrames, int maxLocals)
    {
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
