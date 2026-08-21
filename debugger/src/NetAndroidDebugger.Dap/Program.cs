using System.Globalization;
using NetAndroidDebugger.Dap;

// Values the client reads (0.5, not 0,5) must not depend on the machine's locale.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

// stdout carries the protocol: nothing else may be written to it.
using var input = Console.OpenStandardInput();
using var output = Console.OpenStandardOutput();

await using var adapter = new DapAdapter(new DapConnection(input, output));
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await adapter.RunAsync(cts.Token);
