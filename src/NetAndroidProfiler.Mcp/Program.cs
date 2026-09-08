using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetAndroidProfiler.Mcp;

// Numbers and dates in tool output are read by a machine: keep them culture-invariant.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

// MCP server over stdio: stdout is the protocol channel, logging goes to stderr.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.AddSingleton<SessionHost>();
builder.Services.AddSingleton<GuiHost>();
builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "net-android-profiler", Version = NetAndroidProfiler.Core.Sessions.ProfilerSession.ToolVersion };
        o.ServerInstructions =
            "Profiler for .NET for Android (MonoVM) apps built with -p:EnableDiagnostics=true. " +
            "Typical flow: list_devices -> profile_run (mode sampling|instrumenting|heap; restart or attach) -> " +
            "profile_hotspots / profile_tree / profile_callers / profile_timings / alloc_report / heap_report -> profile_report. " +
            "Long sessions: profile_start ... profile_stop. Results live in a SQLite database per session " +
            "(profile_sessions lists them; any tool accepts sessionId, default = the last session). " +
            "Sampling counts are samples (~1 ms each); *_cpu columns exclude samples of threads blocked in Sleep/Wait. " +
            "A method's exclusive samples include the time of its very short callees: the MonoVM sampler does not report " +
            "tiny leaf methods, so read hotspots as 'this method plus its trivial callees'. " +
            "Instrumenting needs a callspec (e.g. N:My.Namespace) and restarts the app; keep hot leaf methods out of it.";
    })
    .WithStdioServerTransport()
    .WithTools<ProfilerTools>()
    .WithTools<GuiTools>();

await builder.Build().RunAsync();
