using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using NetAndroidDebugger.Mcp;
using NetAndroidProfiler.Mcp;

namespace NetAndroid.Mcp;

/// <summary>
/// The tools of this server: every tool the two product servers define, each registered once,
/// except the three both define (<see cref="SharedTools.Names"/>), which <see cref="SharedTools"/>
/// provides in one version. The product tool classes are used as they are, so a tool added to a
/// product appears here without any change; a tool added to both has to be added to
/// <see cref="SharedTools"/> instead, or the server refuses to start on the duplicate name.
/// </summary>
internal static class ToolCatalog
{
    private static readonly Type[] ProductToolTypes =
        [typeof(DebuggerTools), typeof(DeviceTools), typeof(ProfilerTools), typeof(GuiTools)];

    public static IReadOnlyList<McpServerTool> All()
    {
        var tools = new List<McpServerTool>(FromType(typeof(SharedTools), skip: null));
        foreach (var type in ProductToolTypes)
            tools.AddRange(FromType(type, skip: SharedTools.Names));
        return tools;
    }

    private static IEnumerable<McpServerTool> FromType(Type type, IReadOnlySet<string>? skip)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var attribute = method.GetCustomAttribute<McpServerToolAttribute>();
            if (attribute is null) continue;
            if (skip is not null && skip.Contains(attribute.Name ?? method.Name)) continue;
            // The tool classes are singletons of the host: one instance each, resolved per call.
            yield return McpServerTool.Create(method, context => context.Services!.GetRequiredService(type));
        }
    }
}
