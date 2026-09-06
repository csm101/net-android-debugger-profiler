using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetAndroidDebugger.Mcp;

// Value formatting (doubles, dates) must be stable regardless of the host locale, because the
// output is consumed by a machine (MCP/DAP client), not a human reading it in their culture.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

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
            "are attached automatically; every stop reports its pid and thread id. Screen tools (capture_screenshot, " +
            "get_ui_hierarchy, tap_screen, swipe_screen, press_key, type_text) drive the device through adb so the app " +
            "can be brought to the point worth debugging; check_device_control says what the device allows.";
    })
    .WithStdioServerTransport()
    .WithTools<DebuggerTools>()
    .WithTools<DeviceTools>();

await builder.Build().RunAsync();
