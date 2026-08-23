#!/usr/bin/env bash
# Makes sure one specific emulator is up and booted, and starts it if it is not.
# Idempotent: returns immediately when the serial already answers.
#
#   SERIAL=emulator-5554 AVD=pixel_7_-_api_33_0 bash ensure-emulator.sh
#
# Why this exists: qemu on this machine crashes every few hours with `-gpu host`
# (NVIDIA GL path, see ANDROID_ATTACH_NOTES.md), which fails whole test runs that
# would otherwise pass. Unattended runs call this first.
#
# Notes:
# - Only ever touches the serial it is given; other emulators are left alone.
# - Kills a half-dead instance with `adb emu kill`, and an instance that never registered
#   with adb by its qemu PID - that one is invisible to `adb devices`, so nothing else would
#   clear it, and it blocks the next launch of the same AVD. A non-elevated Stop-Process does
#   kill it, despite what an earlier note here claimed.
# - Defaults to HEADLESS=1 with the hardware GPU. Headless is required once the
#   desktop session is locked or the display sleeps: a windowed emulator then
#   logs "Unable to open monitor interface to \\.\DISPLAY1" and never registers
#   with adb. The hardware GPU still works headless and matters — with
#   `-gpu swiftshader_indirect` the debuggee is slow enough that ordinary
#   property getters exceed the evaluation timeout and the suite turns flaky.
# - After a qemu crash the AVD keeps stale locks (hardware-qemu.ini.lock,
#   multiinstance.lock) and a possibly broken snapshot; both are cleared here.
#   Clearing them does NOT touch installed apps or user data.
set -u
SERIAL=${SERIAL:-emulator-5554}
AVD=${AVD:-pixel_7_-_api_33_0}
PORT=${PORT:-${SERIAL##*-}}
HEADLESS=${HEADLESS:-1}
GPU=${GPU:-host}
CORES=${CORES:-4}
EMULATOR=${EMULATOR:-/c/Program Files (x86)/Android/android-sdk/emulator/emulator.exe}
BOOT_TIMEOUT=${BOOT_TIMEOUT:-300}

# sys.boot_completed stays 1 even when system_server has crashed and is restarting, and a run
# started in that state dies with "Can't find service: package". Ask the package service directly.
booted() {
  [ "$(adb -s "$SERIAL" shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" = "1" ] &&
    adb -s "$SERIAL" shell cmd package list packages >/dev/null 2>&1
}

if booted; then
  echo "$SERIAL already booted"
  exit 0
fi

if adb devices | grep -q "^$SERIAL"; then
  echo "$SERIAL present but not booted/healthy - killing it"
  adb -s "$SERIAL" emu kill >/dev/null 2>&1
  for _ in $(seq 1 15); do adb devices | grep -q "^$SERIAL" || break; sleep 2; done
fi

# An instance that never registered with adb is invisible to the check above, and the next launch
# then dies with "Running multiple emulators with the same AVD". Matched on the AVD name, so other
# emulators are still left alone.
if command -v powershell.exe >/dev/null 2>&1; then
  # The AVD name is escaped and anchored: an unanchored match would kill an emulator whose AVD
  # merely starts with this one's name, and the promise at the top of this file is that other
  # emulators are left alone. Passed through the environment to keep the quoting readable.
  STALE=$(NAD_AVD="$AVD" powershell.exe -NoProfile -Command 'Get-CimInstance Win32_Process | Where-Object { $_.Name -like "qemu*" -and $_.CommandLine -match ("-avd\s+" + [regex]::Escape($env:NAD_AVD) + "(\s|$)") } | Select-Object -ExpandProperty ProcessId' 2>/dev/null | tr -d '\r')
  for stale_pid in $STALE; do
    echo "killing stale qemu $stale_pid for $AVD (it never registered with adb)"
    powershell.exe -NoProfile -Command "Stop-Process -Id $stale_pid -Force" >/dev/null 2>&1
  done
  [ -n "$STALE" ] && sleep 3
fi

AVD_DIR="$HOME/.android/avd/$AVD.avd"
rm -f "$AVD_DIR/hardware-qemu.ini.lock" "$AVD_DIR/multiinstance.lock" "$AVD_DIR/read-snapshot.txt" 2>/dev/null

WINDOW_ARGS=""
[ "$HEADLESS" = "1" ] && WINDOW_ARGS="-no-window -no-audio -no-boot-anim"
echo "starting $AVD on port $PORT (gpu=$GPU, cores=$CORES, headless=$HEADLESS)"
LOG=${LOG:-$(dirname "$0")/ensure-emulator.log}
# shellcheck disable=SC2086
nohup "$EMULATOR" -avd "$AVD" -port "$PORT" -gpu "$GPU" -cores "$CORES" $WINDOW_ARGS -no-snapshot > "$LOG" 2>&1 &

deadline=$(( $(date +%s) + BOOT_TIMEOUT ))
until booted; do
  if [ "$(date +%s)" -ge "$deadline" ]; then
    echo "TIMEOUT: $SERIAL did not boot within ${BOOT_TIMEOUT}s (see $LOG)" >&2
    exit 1
  fi
  sleep 5
done
# Let the boot settle before hammering it with installs/launches.
sleep 20
echo "$SERIAL booted"
