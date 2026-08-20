using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetAndroidDebugger.Mcp;

// MCP server over stdio. stdout carries the protocol; all logging goes to stderr.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.AddSingleton<SessionHost>();
builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "net-android-debugger", Version = "0.1.0" };
        o.ServerInstructions =
            "Debugger for .NET for Android (MonoVM) apps. Typical flow: list_devices -> launch_app " +
            "(deploy optional) -> set_breakpoint -> wait_until_stopped / continue_and_wait -> get_locals / " +
            "get_call_stack / evaluate_expression -> step_* -> terminate_app. Helper processes of the app " +
            "are attached automatically; every stop reports its pid and thread id.";
    })
    .WithStdioServerTransport()
    .WithTools<DebuggerTools>();

await builder.Build().RunAsync();
