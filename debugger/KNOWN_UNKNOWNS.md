# Known unknowns

Open questions that block or condition the work. When one is resolved, move the
answer into the owning document (ANDROID_ATTACH_NOTES.md, ARCHITECTURE.md,
PROJECT_STATE.md, TEST_CATALOG.md) and delete the entry.

## U1 - debugger-libs consumption
Submodule vs pruned source copy? Which commit? Does it build on net8.0 without
patches? How is the Mono.Cecil dependency satisfied today (repo expects cecil
cloned side by side)? What did vscode-mono-debug PR #37 (replace NuGet packages
with source code) keep?

## U2 - AndroidAttachDebugger runtime behavior
With AndroidAttachDebugger=true, does the app block waiting for the debugger?
Is there a timeout? Who listens and who connects (app agent listens on
TargetPort and host connects through the forward, or the reverse)? What happens
on debugger disconnect - does the app resume or die? Emulator vs physical
device differences.

## U3 - Mono.Debugging.Soft attach API for this scenario
Exact SoftDebuggerSession startup args (SoftDebuggerConnectArgs vs
SoftDebuggerListenArgs) matching the direction found in U2. Which SDB protocol
version does the net9-android agent speak, and does vendored debugger-libs
accept it?

## U4 - Fast test tier without emulator
Integration tests need a live SDB agent. Emulator boot is slow. Is there a
faster host-side target? (Desktop Mono not installed; .NET on Windows does not
run MonoVM.) Candidates: one always-booted emulator, headless x86_64 emulator
in CI, or device-attached runs only. Decide during M0/M1 and record the
test-run contract in TEST_CATALOG.md.

## U5 - Expression evaluation scope
What does the Mono.Debugging built-in evaluator cover on net9-android
(properties invoking code, generics, async frames, linker-stripped members)?
Where do we need our own formatting (the Delphi project needed a lot)?

## U6 - the reference application debug build specifics
Does App.Droid Debug config have fast deployment enabled? Custom manifest
flags, multi-process, services starting before attach? MQTT/watchdog behavior
while paused at a breakpoint.

## U7 - MCP C# SDK
Official ModelContextProtocol NuGet package: current version, API stability,
stdio transport fit. Fallback: hand-rolled newline-delimited JSON-RPC 2.0 like
the Delphi MCP server (small, proven).

## U8 - CoreCLR on Android
Future .NET versions may switch Android to CoreCLR (SDB disappears). Not a
near-term concern for net9; track when the reference application retargets.

## U9 - Physical palmari over WiFi adb
Attach flow against real handhelds over adb connect host:port, possibly
through SSH tunnels. Latency/stability of SDB over that path.
