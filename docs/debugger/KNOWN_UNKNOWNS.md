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
Mono is gone in `net11.0-android`: apps built for it run on CoreCLR, and the Mono Soft
Debugger protocol this engine speaks (with `ThirdParty/debugger-libs`) does not exist
there. The engine stays valid for `net10.0-android` apps until November 2028, when .NET 10
leaves support, and for the reference application (`net9.0-android35.0`) until it retargets. A CoreCLR engine
needs a different wire protocol and a different debug-property mechanism (`src/native/clr/`
in dotnet/android). The repository-level view and the order of decisions are in
`docs/KNOWN_UNKNOWNS.md` R2. Not a near-term concern for the reference application.

## U9 - Physical handhelds over WiFi adb
Attach flow against real handhelds over adb connect host:port, possibly
through SSH tunnels. Latency/stability of SDB over that path.

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
What is still unknown: which property of the the reference application frame (async state machine in a
symbol-less third-party assembly, rethrow through a continuation, or something
else) makes `GetException()` return nothing. The fallback covers it either way.

Measured on the reference application, 2026-08-23, and it is worse than recorded above. With a
catch-all `log` rule and a `System.Exception` filter, the debugger output shows
both halves of the problem:

    [pid 25820] exception rule: Java.Security.Cert.CertificateException (not stopping)
    capturing exception type failed: Object reference not set to an instance of an object.
    [pid 25820] exception rule:  (not stopping)

`bt.GetFrame(0).GetException()` throws NullReferenceException from inside
Mono.Debugging for exceptions raised in an assembly without symbols. The guard
keeps the stop, but the type stays empty - and **the rules are then matched
against that empty type**, so `type`/`typeContains` cannot match. the reference application loads
`MQTTnet` and `MQTTnet.Extensions.ManagedClient` with NO SYMBOLS (confirmed via
get_loaded_assemblies), which means the rule engine is blind exactly on the
exceptions that motivated it.

The `$exception` fallback added earlier does not help here: it lives in
`GetExceptionDetails`, which runs when someone asks for details after a stop.
By then the rule has already decided.

Fix to make: treat a missing type like a rule that needs the message - hold the
stop and resolve it on a worker (`ResolveExceptionTypeLive`) before matching,
since reading it means invoking in the debuggee and that is forbidden on the
event thread. The machinery for deciding off the event thread already exists
(`RulesNeedMessage`); the condition needs to grow a "type is missing and some
rule cares about the type" case.

**Fixed for the rules, 2026-08-23.** `RulesNeedTheDebuggee` now sends the
decision to a worker when the stop carried no type *and* some rule cares about
the type; the worker recovers it through `ResolveExceptionTypeLive` before
matching, and puts it back into the reported stop. Verified on the reference application with
`DevTools/ExceptionTypeProbe` - the same log point that used to read

    capturing exception type failed: Object reference not set to an instance of an object.
    [pid 25820] exception rule:  (not stopping)

now reads

    capturing exception type failed: Object reference not set to an instance of an object.
    [pid 28511] exception rule: System.Threading.Tasks.TaskCanceledException (not stopping)

Still unknown, and still worth this entry: *why* `GetException()` throws a
NullReferenceException inside Mono.Debugging for a throw site without debug
info. The engine now works around it rather than through it, and a rule that
names no type still decides on the event thread, so the workaround costs
nothing when it is not needed.

Also learned while measuring: the reference application's recurring first-chance exceptions are
`Java.Security.Cert.CertificateException` (repeatedly, from startup),
`SQLite.SQLiteException` (three times during startup) and
`TaskCanceledException` after a resume - not the MQTT ones, which need the
client to be connected first. MQTTnet takes minutes to load, so a probe that
suspends the app right after launch proves nothing: there is no connection yet
to time out.

Confirmed again through the MCP server (2026-08-23, after republishing), which
is the path a user actually takes: six exceptions whose type the capture could
not read came back typed - `TaskCanceledException`, `InvalidOperationException`
(twice) and `OperationCanceledException`.

Worth knowing when writing rules for this app: **the MQTT exceptions are not
named after MQTT.** What arrives after a suspend/resume is
`System.Threading.Tasks.TaskCanceledException`,
`System.InvalidOperationException` (the "connect/disconnect is pending" one) and
`System.OperationCanceledException`. A rule with `typeContains: "Mqtt"` - the
example in the tool description and in the probe - matches none of them. Use
`messageContains` or `sourceFileContains` for this app instead.
