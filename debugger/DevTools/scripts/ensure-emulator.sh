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
# - Kills a half-dead instance with `adb emu kill` (killing the qemu PID from a
#   non-elevated shell silently fails and blocks the next launch of the same AVD).
set -u
SERIAL=${SERIAL:-emulator-5554}
AVD=${AVD:-pixel_7_-_api_33_0}
PORT=${PORT:-${SERIAL##*-}}
GPU=${GPU:-host}
CORES=${CORES:-4}
EMULATOR=${EMULATOR:-/c/Program Files (x86)/Android/android-sdk/emulator/emulator.exe}
BOOT_TIMEOUT=${BOOT_TIMEOUT:-300}

booted() { [ "$(adb -s "$SERIAL" shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" = "1" ]; }

if booted; then
  echo "$SERIAL already booted"
  exit 0
fi

if adb devices | grep -q "^$SERIAL"; then
  echo "$SERIAL present but not booted/healthy - killing it"
  adb -s "$SERIAL" emu kill >/dev/null 2>&1
  for _ in $(seq 1 15); do adb devices | grep -q "^$SERIAL" || break; sleep 2; done
fi

echo "starting $AVD on port $PORT (gpu=$GPU, cores=$CORES)"
LOG=${LOG:-$(dirname "$0")/ensure-emulator.log}
nohup "$EMULATOR" -avd "$AVD" -port "$PORT" -gpu "$GPU" -cores "$CORES" -no-snapshot-load > "$LOG" 2>&1 &

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
