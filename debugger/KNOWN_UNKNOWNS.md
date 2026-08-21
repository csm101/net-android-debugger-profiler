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
Answered (details in ANDROID_ATTACH_NOTES.md, the reference application section): Debug build
is debuggable with fast deployment, attach works, multi-process handled, and
pausing a fully operational installation (logged on, backend reachable, MQTT
up) for 5 minutes is harmless - no ANR, no process death, MQTT reconnects
itself after a single failed attempt, and the sync watchdog neither restarts
threads nor emits a bug report. Not measured: pauses of tens of minutes (the
watchdog could still fire and send a real bug report), and the on-demand
`the app's own android:process` process - reopen a narrower entry if either matters.

## U8 - CoreCLR on Android
Future .NET versions may switch Android to CoreCLR (SDB disappears). Not a
near-term concern for net9; track when the reference application retargets.

## U9 - Physical palmari over WiFi adb
Attach flow against real handhelds over adb connect host:port, possibly
through SSH tunnels. Latency/stability of SDB over that path.

## U11 - What an aborted debuggee invocation costs (mostly answered)
Measured 2026-08-20 with TestTarget `SlowProbe.SlowValue` (an 8 s getter) and
a 1.5 s timeout. The debugger side is settled: the timeout is reported as an
error, the session stays responsive, later evaluation and expansion answer
normally, and `RunBounded` guarantees no hang.
The **debuggee** side is not deterministic: an invocation stuck in a call the
abort cannot interrupt (here `Thread.Sleep`) sometimes survives - the thread
keeps working and Continue resumes it - and sometimes the runtime retries the
abort repeatedly ("Aborting invocation of ..." over and over) until the whole
process dies. Both outcomes were observed on the same test.
Practical consequence, worth documenting for users rather than fixing: keep
`EvaluationTimeout` generous, and on a target where a getter may block, set
`allowTargetInvoke=false` (or `allowToStringCalls=false`) instead of relying
on the abort. Open only if it ever matters: can the engine tell an
interruptible invocation from a doomed one before starting it? Covered by
`AbortedSlowInvoke_LeavesTheThreadUsable`.

## U12 - DECIDED (delete once the entry stops being useful context)
An unhandled exception suspends the debuggee at ExceptionDispatchInfo.Throw,
and while the process dies the runtime raises further unhandled exceptions on
other threads, each of which suspended it again - so "Continue once and the
app exits" did not hold. Decision (implemented in DebugSession, 2026-08-20):
only the FIRST unhandled exception per process is reported; later ones are
resumed automatically and logged, and the reported details keep describing the
first one. One Continue is therefore enough. Note the session still reports
Exited only when every process is gone, and the sticky `:helper` service can
outlive the crashing main process.

## U13 - Hit-count breakpoints count from an unstable baseline
`HitCountBreakpoint_StopsAtNthHit` asks for the 3rd hit and usually gets it,
but once in several runs the stop arrived on the 9th (suite run 28b). The
breakpoint was already resolved before the first hit, so it is not a late
arming.
Narrowed by reading upstream (2026-08-21): `CurrentHitCount` lives on the
shared `BreakEvent`, and there is exactly **one** code path that zeroes it —
`DebuggerSession.Breakpoints`'s setter, which calls `ResetBreakpoints()` on
the store the session had *before*. `ProcessDebugger` assigns the store once
per session, on a session that has none yet, so the "second process attaching
resets the counter" hypothesis is not confirmed: the reset would land on the
empty store the getter auto-creates, not on ours. Toggling `Enabled` (what the
evaluation disarm does) does not reset anything either.
What remains plausible is that hits are **missed** rather than reset: a hit
that lands while a break event is being registered, re-registered or disabled
in one of the sessions is never counted.
Deliberately not chased further: the test asserts "at least N hits" and the
limitation is documented for users (hit counts are approximate in
multi-process apps). Settle it with data, not more reading, if it ever
matters — log `CurrentHitCount` per stop across many runs.

## U14 - Can port rotation be made race-free?
`debug.mono.extra` is device-global and read at process start; the launcher
rotates it to the next port when it *sees* an agent-init line in logcat. Two
processes starting within the same instant therefore read the same value, take
the same port, and the second one's agent cannot listen - it dies (observed
2026-08-21, details in ANDROID_ATTACH_NOTES).
Options, none obviously right:
- rotate on ActivityManager's `Start proc` line instead of on agent init. That
  line comes earlier, but the runtime reads the property after it, so rotating
  then risks stealing the port from the process that is about to read it.
- rotate on a timer while any process of the app is starting.
- accept the collision and recover: two pids announcing the same port is
  detectable, but by then the loser's agent has already failed.
Related, and measured: the decision "is this pid ours?" happens on that same
thread before the rotation, so it must stay fast. A 400 ms retry added there on
2026-08-21 was enough to make `:helper` lose its port and fail the handshake in
three different tests.
Not worth solving until an app is actually hurt by it: processes normally start
seconds apart, and the tests that used to trip it now sequence themselves.

## U15 - Exception type is empty when the throw site has no debug info
Seen live on the reference application (2026-08-21): a first-chance stop on
`MQTTnet.Client.MqttClient.ConnectAsync` reported an empty exception type - the
stop line read `exception= message=` - while `GetExceptionDetails` still
produced the message and the stack. The type captured on the event thread comes
from `GetException()` on the stopping frame, and that frame belongs to an
assembly shipped without symbols.
Fixed by resolving the type from the `$exception` value's own type name on the
caller thread when the captured one is empty (no debuggee invocation involved).
Not reproduced in TestTarget, and the attempt is worth recording so nobody
repeats it: a symbol-less helper assembly whose method throws was added, and the
type came through correctly from `GetException()` anyway - so that shape is not
what defeats it. Making the throw `async` inside that assembly did break the
test, but for an unrelated reason: the broadcast receiver driving it hung (60 s
`BroadcastQueue` timeout) with the app otherwise healthy and ticking, and the
cause was not found. The scaffolding was reverted rather than left in place
hanging the debuggee.
What is still unknown: which property of the V7 frame (async state machine in a
symbol-less third-party assembly, rethrow through a continuation, or something
else) makes `GetException()` return nothing. The fallback covers it either way.
