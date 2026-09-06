using NetAndroidDebugger.Core;

// Does a type rule still match when the throw site has no debug info?
//
// KNOWN_UNKNOWNS U15: an exception raised in an assembly without symbols reaches the stop with an
// empty type, and the rules used to be matched against that emptiness — so `typeContains` could
// never fire on exactly the third-party noise it exists to silence. This drives a real app, sets a
// type rule, and prints what the engine decided.
//
//   dotnet run --project DevTools/ExceptionTypeProbe -- <serial> <package> [suspendSeconds] [watchSeconds]
//
// Provoking the exceptions: suspending the app makes its MQTT client time out, and it reconnects on
// resume (measured, see ANDROID_ATTACH_NOTES.md). That is the deterministic way in without touching
// the network.

var serial = args.ElementAtOrDefault(0) ?? "emulator-5554";
var package = args.ElementAtOrDefault(1) ?? "App.Droid";
var suspendSeconds = int.TryParse(args.ElementAtOrDefault(2), out var s) ? s : 50;
var watchSeconds = int.TryParse(args.ElementAtOrDefault(3), out var w) ? w : 45;

var interesting = new List<string>();
var mqttLoaded = false;
void Log(string line)
{
    // Everything is printed, but the lines that answer the question are collected as well, because
    // an app this size buries them under assembly loads.
    Console.WriteLine(line);
    if (line.Contains("MQTTnet", StringComparison.OrdinalIgnoreCase)) mqttLoaded = true;
    if (line.Contains("exception rule:", StringComparison.Ordinal)
        || line.Contains("capturing exception type failed", StringComparison.Ordinal)
        || line.Contains("exception type via", StringComparison.Ordinal))
    {
        interesting.Add(line);
    }
}

await using var session = new DebugSession(Log);
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(12));

Console.WriteLine($"launching {package} on {serial}");
await session.LaunchAsync(new AppTarget(package), new LaunchOptions(serial, KeepPropertyFresh: true), cts.Token);

// A rule that can only work if the type is known, plus a catch-all so everything else is visible.
session.SetExceptionRules(
[
    new ExceptionRule(ExceptionAction.Ignore, TypeContains: "Mqtt"),
    new ExceptionRule(ExceptionAction.Log),
]);
session.SetExceptionFilters(new ExceptionFilters(FirstChanceTypes: ["System.Exception"]));
Console.WriteLine("rules: ignore *Mqtt*, log everything else; filter: System.Exception");

// Suspending during startup proves nothing: the client is not connected yet, so there is no
// timeout to provoke. Wait until the app actually loads MQTTnet before holding it.
Console.WriteLine("waiting for the MQTT client to be loaded ...");
var readyBy = DateTime.UtcNow + TimeSpan.FromMinutes(5);
while (DateTime.UtcNow < readyBy && !mqttLoaded)
    await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
Console.WriteLine(mqttLoaded ? "MQTTnet loaded; giving it a moment to connect" : "MQTTnet never loaded; suspending anyway");
await Task.Delay(TimeSpan.FromSeconds(15), cts.Token);

Console.WriteLine($"suspending for {suspendSeconds}s to make the MQTT client time out");
await session.PauseAsync(TimeSpan.FromSeconds(10), cts.Token);
await Task.Delay(TimeSpan.FromSeconds(suspendSeconds), cts.Token);

Console.WriteLine("resuming");
await session.ContinueAndWaitAsync(TimeSpan.FromSeconds(watchSeconds), cts.Token);
await Task.Delay(TimeSpan.FromSeconds(watchSeconds), cts.Token);

Console.WriteLine();
Console.WriteLine("---- what the rules decided ----");
foreach (var line in interesting) Console.WriteLine(line);
Console.WriteLine($"---- {interesting.Count} line(s) ----");

await session.TerminateAsync(CancellationToken.None);
