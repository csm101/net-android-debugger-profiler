# Known unknowns

Open questions that block or condition the work. When one is resolved, move the
answer into the owning document (ANDROID_ATTACH_NOTES.md, ARCHITECTURE.md,
PROJECT_STATE.md, TEST_CATALOG.md) and delete the entry.

## U2 - Attach behavior on physical devices / the reference application (emulator part resolved)
Emulator behavior is fully recorded in ANDROID_ATTACH_NOTES.md. Still open:
physical device differences (clock skew host/device affects the freshness
deadline - always compute it from `adb shell date +%s`), behavior of the
net9.0-android35.0 runtime used by the reference application (expected identical, unverified),
what exactly `RunActivity` passes besides `debug.mono.extra` (Java `-D`?
user id?) - read dotnet/android `RunActivity.cs` if it ever matters.

## U4 - Fast test tier without emulator
Integration tests need a live SDB agent. Emulator boot is slow. Is there a
faster host-side target? (Desktop Mono not installed; .NET on Windows does not
run MonoVM.) Candidates: one always-booted emulator, headless x86_64 emulator
in CI, or device-attached runs only. Decide during M0/M1 and record the
test-run contract in TEST_CATALOG.md. Data point (2026-08-20): emulator boot
from snapshot to `sys.boot_completed=1` took well under a minute; a full
restart-app + connect + breakpoint cycle takes ~8 s, so "one booted emulator,
restart the app per test" is viable.

## U5 - Expression evaluation scope
What does the Mono.Debugging built-in evaluator cover on net9-android
(properties invoking code, generics, async frames, linker-stripped members)?
Where do we need our own formatting (the Delphi project needed a lot)?

## U6 - the reference application debug build specifics
Resolved (ANDROID_ATTACH_NOTES.md, the reference application section): Debug build is
debuggable with fast deployment, attach works, multi-process handled, and a
4.5-minute pause on the UI thread kills nothing (no ANR, both processes
survive, no watchdog restart in the 3.5 min after resume).
Still open, and NOT answerable on this emulator (it cannot reach the the reference application
backend `an internal backend host`, so sync threads never run
normally): what a long pause does to a *working* installation - does MQTT
auto-reconnect cleanly, and does the watchdog's wall-clock logic fire on
resume and send spurious bug reports (source says it can:
`WatchDog.DevoRiavviareIThread` -> `PrepareBugReport(...).Send()` + restart
all threads, skipped only while `NeedManualLogon`)? Needs a logged-on device
on a network that reaches the backend. Also still open: the on-demand
`the app's own android:process` process.

## U8 - CoreCLR on Android
Future .NET versions may switch Android to CoreCLR (SDB disappears). Not a
near-term concern for net9; track when the reference application retargets.

## U9 - Physical palmari over WiFi adb
Attach flow against real handhelds over adb connect host:port, possibly
through SSH tunnels. Latency/stability of SDB over that path.

## U11 - Recovering from a wedged evaluation thread
After an aborted invoke (timeout) on the stopped thread, all further invokes
on that thread fail (ANDROID_ATTACH_NOTES.md, "Method invocation ... can
wedge"). Open: does a Continue + next stop heal it (new invoke context) or is
the thread unusable for the rest of the process lifetime? Can invokes be
routed to another stopped thread? Is it specific to culture/ICU init (first
DateTime.ToString) - if so, a one-time warm-up invoke right after attach
(e.g. evaluate `System.DateTime.Now.ToString()` with a long timeout while
nothing else is pending) would remove the trigger. Measure on the emulator
and on a real device before designing more.

## U12 - Continue after an unhandled exception; multiple unhandled stops
An unhandled exception suspends the debuggee at ExceptionDispatchInfo.Throw.
Observed (suite run 16): after Continue the runtime raises a SECOND
UnhandledException on another thread (thread 5, e.g. inside monitor/teardown)
which re-suspends before the process dies; sometimes the process instead dies
immediately at the first stop. So "Continue once -> app exits" is not reliable.
Open: should the engine auto-continue repeated unhandled-exception stops until
the process exits (with a cap), or expose them as distinct stops? For now the
frontend/user resumes until Exited, or calls terminate. Decide when a real app
scenario needs it; the SDB semantics (can an unhandled exception even be
resumed meaningfully?) need confirming against dotnet/android behavior.
