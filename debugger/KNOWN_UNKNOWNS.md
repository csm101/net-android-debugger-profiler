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
arming. Working hypothesis: the Mono `BreakpointStore` is shared by every
process of the app (one store, N sessions), so when a second process attaches
the breakpoint is re-registered and Mono's `CurrentHitCount` restarts - which
also matches the variability (it depends on when `:helper` connects).
To confirm: log `CurrentHitCount` per stop, or give each process its own
store and see whether the count becomes stable. Until then the test asserts
"at least N hits" and the limitation is documented for users: hit counts are
approximate in multi-process apps.
