using System.Globalization;
using System.Runtime.CompilerServices;

namespace NetAndroidDebugger.Tests.Harness;

internal static class TestInit
{
    // The engine formats values with the ambient culture; the shipping MCP server forces
    // InvariantCulture (Program.cs). Do the same for the test process so assertions on numeric
    // and date formatting are locale-independent (this machine runs it-IT: "0,5" vs "0.5").
    [ModuleInitializer]
    public static void Init()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    }
}
