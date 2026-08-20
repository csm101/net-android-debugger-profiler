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
# - Defaults to HEADLESS=1 with a software GPU, because a windowed `-gpu host`
#   emulator cannot start once the desktop session is locked or the display is
#   asleep ("Unable to open monitor interface to \\.\DISPLAY1" and it never
#   registers with adb). Set HEADLESS=0 GPU=host for an interactive, faster one.
# - After a qemu crash the AVD keeps stale locks (hardware-qemu.ini.lock,
#   multiinstance.lock) and a possibly broken snapshot; both are cleared here.
#   Clearing them does NOT touch installed apps or user data.
set -u
SERIAL=${SERIAL:-emulator-5554}
AVD=${AVD:-pixel_7_-_api_33_0}
PORT=${PORT:-${SERIAL##*-}}
HEADLESS=${HEADLESS:-1}
GPU=${GPU:-swiftshader_indirect}
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
