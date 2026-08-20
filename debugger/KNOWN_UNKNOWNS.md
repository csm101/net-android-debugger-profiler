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
Resolved so far (ANDROID_ATTACH_NOTES.md, the reference application section): Debug build is
debuggable with fast deployment, attach works, multi-process handled. Still
open: MQTT/watchdog/TTManager behavior while paused at a breakpoint for a
long time (does the app get killed or reconnect storms happen?), and the
on-demand `the app's own android:process` process.

## U8 - CoreCLR on Android
Future .NET versions may switch Android to CoreCLR (SDB disappears). Not a
near-term concern for net9; track when the reference application retargets.

## U9 - Physical palmari over WiFi adb
Attach flow against real handhelds over adb connect host:port, possibly
through SSH tunnels. Latency/stability of SDB over that path.
