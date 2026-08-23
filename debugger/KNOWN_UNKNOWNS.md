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

## U12 - Unhandled exceptions: reported once, death not guaranteed
An unhandled exception suspends the debuggee at ExceptionDispatchInfo.Throw, and
while the process dies the runtime raises further unhandled exceptions on other
threads, each of which suspended it again - so "Continue once and the app exits"
did not hold. Decision (implemented in DebugSession, 2026-08-20): only the FIRST
unhandled exception per process is reported; later ones are resumed
automatically and logged, and the reported details keep describing the first
one. One Continue is therefore enough.
**Corrected 2026-08-21:** the process does *not* reliably die afterwards.
Measured over eight runs on the emulator: usually gone within seconds, but twice
still alive past 90 s - with the crash hook armed (a tick throwing on every
iteration) and with it disarmed alike. `UnhandledException_IsReported_ThenAppExits`
therefore accepts both outcomes and asserts what does hold: the exception is
reported once with its details, one Continue is enough, the crashed process is
never left held stopped, and the session never claims to be Stopped with nothing
suspended. Whether Mono's own teardown is being interfered with by the automatic
resume of later unhandled exceptions is not established.
Note the session still reports Exited only when every process is gone, and the
sticky `:helper` service can outlive the crashing main process.
## U13 - Hit-count breakpoints: measured, no longer reproducing
`HitCountBreakpoint_StopsAtNthHit` asks for the 3rd hit and once in several runs
stopped on the 9th (suite run 28b, 2026-08-20). `CurrentHitCount` lives on the
shared `BreakEvent`, and the only upstream path that zeroes it is
`DebuggerSession.Breakpoints`'s setter, which `ProcessDebugger` takes once per
session on a session that has no store yet - so the "second process attaching
resets the counter" hypothesis was never confirmed. What remained plausible was
that hits are *missed* while a break event is being registered in one of the
sessions.
Measured 2026-08-21, after the port-rotation work: `ProcessDebugger` now logs
`hit count now N (stops at M)` at every hit-count stop, and six consecutive runs
were exact - count 3, tick 3, every time. The anomaly has not reappeared since
processes stopped colliding on ports (U14) and since a launch waits for the old
processes to be gone: both of those used to make a second process attach at an
unpredictable moment, which is exactly the window suspected here.

Update 2026-08-23: `HitCondition_EveryNthHit_StopsOnAMultiple` failed once in a
full run and passed in isolation - but that test was asserting on the app's own
`_ticks`, not on the hit count. Those two can legitimately disagree: `Tick` runs
on a `System.Threading.Timer`, which does not serialise its callbacks, so two
ticks can overlap and `_ticks++` is not atomic. The message was lost with the
run's `-v q` output, so this is not proof of what failed. What it does buy: the
test now asserts on the engine's own `hit count now N` diagnostic, which is what
the feature promises, so that way of failing no longer exists. **If it fails
again, it is this unknown for real.**
Not closed, because the original was rare and six runs cannot prove its absence.
The test still asserts "at least N hits" and the diagnostic stays: if it ever
comes back, the log line says immediately whether the count was reset or the
hits were never counted.
## U14 - RESOLVED 2026-08-21 (delete once it stops being useful context)
`debug.mono.extra` is device-global and read at process start; the launcher used
to rotate it only when it *saw* an agent-init line in logcat. Two processes
starting between those two moments read the same value, took the same port, and
the loser's agent could not listen - it died, sometimes taking the app with it
(`no SDB handshake on port N within 20s`). It stopped being theoretical: it was
the top cause of suite flakiness, hitting a different test each run.
Fixed by rotating at **process start** as well: ActivityManager logs
`Start proc <pid>:<name>/<uid>` at fork, long before the runtime reads the
property, so the window shrinks from hundreds of milliseconds to almost nothing.
The worry recorded here earlier - that rotating early would steal the port from
the process about to read it - was unfounded: that process reads whatever is
current, and its agent-init line tells us which port it actually took. Rotation
is also idempotent now (a port already behind us leaves the property alone), so
being called at both moments costs one write, not two.
Related and still true: the decision "is this pid ours?" happens on the logcat
thread before the rotation, so it must stay fast. A 400 ms retry added there was
enough to break three tests.
Not fully closed: two processes that fork in the very same instant can still
collide. Nothing observed since the fix.
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
