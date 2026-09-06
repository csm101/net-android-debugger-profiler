using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetAndroid.Mcp;

// Numbers and dates in tool output are read by a machine: keep them culture-invariant.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

// MCP server over stdio: stdout is the protocol channel, logging goes to stderr.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Information);

// One process, both engines: each product's session host and tool classes as they are, one
// instance each, plus what only the union needs - the shared tools and the device arbiter.
builder.Services.AddSingleton<NetAndroidDebugger.Mcp.SessionHost>();
builder.Services.AddSingleton<NetAndroidProfiler.Mcp.SessionHost>();
builder.Services.AddSingleton<NetAndroidDebugger.Mcp.DebuggerTools>();
builder.Services.AddSingleton<NetAndroidDebugger.Mcp.DeviceTools>();
builder.Services.AddSingleton<NetAndroidProfiler.Mcp.ProfilerTools>();
builder.Services.AddSingleton<SharedTools>();
builder.Services.AddSingleton<DeviceArbiter>();
builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = UnifiedServer.Name, Version = UnifiedServer.Version };
        o.ServerInstructions = UnifiedServer.Instructions;
    })
    .WithStdioServerTransport()
    .WithTools(ToolCatalog.All())
    .WithRequestFilters(filters => filters.AddCallToolFilter(DeviceArbiter.Filter));

await builder.Build().RunAsync();
