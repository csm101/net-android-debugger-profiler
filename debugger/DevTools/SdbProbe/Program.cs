// SdbProbe: connects Mono.Debugging.Soft to an SDB agent reachable on localhost
// (e.g. through `adb forward`), sets one source-line breakpoint, waits for it to
// hit, dumps threads / backtrace / locals, then continues and exits after N hits.
//
// Usage:
//   SdbProbe --port 10000 --file C:\path\MainActivity.cs --line 42 [--hits 2] [--timeout 90] [--app name]
//
// Everything comes from argv. Nothing here knows about TestTarget.

using System.Net;
using Mono.Debugging.Client;
using Mono.Debugging.Soft;

int port = 10000;
string? file = null;
int line = 0;
int hits = 2;
int timeoutSec = 90;
string appName = "sdb-probe";

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port": port = int.Parse(args[++i]); break;
        case "--file": file = args[++i]; break;
        case "--line": line = int.Parse(args[++i]); break;
        case "--hits": hits = int.Parse(args[++i]); break;
        case "--timeout": timeoutSec = int.Parse(args[++i]); break;
        case "--app": appName = args[++i]; break;
        default: Console.Error.WriteLine($"unknown arg {args[i]}"); return 2;
    }
}

if (file is null || line <= 0)
{
    Console.Error.WriteLine("required: --file <path> --line <n>");
    return 2;
}

var sw = System.Diagnostics.Stopwatch.StartNew();
void Log(string msg) => Console.WriteLine($"[{sw.Elapsed.TotalSeconds,7:F3}] {msg}");

var session = new SoftDebuggerSession();
var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
int hitCount = 0;

session.ExceptionHandler = ex => { Log($"EXCEPTION HANDLER: {ex}"); return true; };
session.LogWriter = (isStderr, text) => Log($"LOG{(isStderr ? "(err)" : "")}: {text.TrimEnd()}");
session.OutputWriter = (isStderr, text) => Log($"OUT{(isStderr ? "(err)" : "")}: {text.TrimEnd()}");

session.TargetEvent += (_, e) => Log($"TargetEvent {e.Type} thread={e.Thread?.Id} {e.Thread?.Name}");
session.TargetStarted += (_, _) => Log("TargetStarted");
session.TargetReady += (_, _) => Log("TargetReady (connected)");
session.TargetExited += (_, _) => { Log("TargetExited"); done.TrySetResult(1); };
session.TargetInterrupted += (_, e) => Log("TargetInterrupted");
session.TargetExceptionThrown += (_, e) => Log($"TargetExceptionThrown");
session.TargetUnhandledException += (_, e) => Log($"TargetUnhandledException");
session.AssemblyLoaded += (_, e) => Log($"AssemblyLoaded {e.Assembly}");
session.TargetThreadStarted += (_, e) => Log($"ThreadStarted {e.Thread?.Id} {e.Thread?.Name}");
session.TargetThreadStopped += (_, e) => Log($"ThreadStopped {e.Thread?.Id} {e.Thread?.Name}");

session.TargetHitBreakpoint += (_, e) =>
{
    hitCount++;
    Log($"=== BREAKPOINT HIT #{hitCount} on thread {e.Thread?.Id} '{e.Thread?.Name}'");
    try
    {
        var procs = session.GetProcesses();
        foreach (var p in procs)
        {
            Log($"process {p.Id} '{p.Name}'");
            foreach (var t in p.GetThreads())
                Log($"  thread {t.Id} '{t.Name}' @ {t.Location}");
        }

        var bt = e.Backtrace;
        Log($"backtrace frames: {bt.FrameCount}");
        for (int i = 0; i < bt.FrameCount; i++)
        {
            var f = bt.GetFrame(i);
            var loc = f.SourceLocation;
            Log($"  #{i} {loc?.MethodName} {loc?.FileName}:{loc?.Line} ext={f.IsExternalCode} dbg={f.HasDebugInfo}");
        }

        var top = bt.GetFrame(0);
        foreach (var v in top.GetAllLocals())
        {
            if (v.IsEvaluating) v.WaitHandle.WaitOne(5000);
            Log($"  local {v.Name} : {v.TypeName} = {v.Value} (display '{v.DisplayValue}', err={v.IsError})");
        }
    }
    catch (Exception ex)
    {
        Log($"inspection failed: {ex}");
    }

    if (hitCount >= hits)
    {
        done.TrySetResult(0);
    }
    else
    {
        Log("continue");
        session.Continue();
    }
};

var bp = session.Breakpoints.Add(file, line);
Log($"breakpoint requested {bp.FileName}:{bp.Line}");

var startInfo = new SoftDebuggerStartInfo(new SoftDebuggerConnectArgs(appName, IPAddress.Loopback, port));
var options = new DebuggerSessionOptions
{
    EvaluationOptions = EvaluationOptions.DefaultOptions,
    ProjectAssembliesOnly = false,
};

Log($"connecting to 127.0.0.1:{port} ...");
session.Run(startInfo, options);

var winner = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSec)));
int exit;
if (winner == done.Task)
{
    exit = done.Task.Result;
    Log($"done, exit {exit}");
}
else
{
    exit = 3;
    Log($"TIMEOUT after {timeoutSec}s; hits={hitCount} connected={session.IsConnected} running={session.IsRunning}");
}

try
{
    Log("detaching");
    session.Detach();
}
catch (Exception ex) { Log($"detach failed: {ex.Message}"); }
session.Dispose();
return exit;
