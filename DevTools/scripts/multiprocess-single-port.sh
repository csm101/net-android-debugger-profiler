#!/usr/bin/env bash
# Experiment A behind ANDROID_ATTACH_NOTES.md "Multi-process apps": single port, no rotation.
# Starts the app with the agent on one port and debugs only the main process; shows what
# happens to helper processes that read the same property (bind failure, die, respawn loop).
#
# Usage:
#   PKG=<package> MAINBP=<abs file>:<line> [PORT=10000] \
#   [PROBE=./DevTools/SdbProbe/bin/Debug/net10.0/SdbProbe.exe] bash multiprocess-single-port.sh <logdir>
# Needs: app deployed, adb on PATH, SdbProbe built. Leaves the property cleared and the app stopped.
set -u
# Never rely on the default adb device: other emulators/devices may be attached. adb honors ANDROID_SERIAL.
: "${ANDROID_SERIAL:?set ANDROID_SERIAL to the target device serial}"
PKG=${PKG:?package}; MAINBP=${MAINBP:?file:line}
PORT=${PORT:-10000}
PROBE=${PROBE:-./DevTools/SdbProbe/bin/Debug/net10.0/SdbProbe.exe}
LOG=${1:?logdir}

ACT=$(adb shell cmd package resolve-activity --brief "$PKG" | tail -1 | tr -d '\r')
T=$(( $(adb shell date +%s | tr -d '\r') + 300 ))
adb shell am force-stop "$PKG"
adb shell setprop debug.mono.extra "debug=127.0.0.1:$PORT,timeout=$T,loglevel=0,server=y"
adb forward tcp:$PORT tcp:$PORT >/dev/null
adb logcat -c
adb shell am start -n "$ACT" >/dev/null
sleep 3
echo "== processes at +3s"; adb shell ps -A | grep "$PKG" | awk '{print $2, $NF}'
"$PROBE" --port $PORT --file "${MAINBP%:*}" --line "${MAINBP##*:}" --hits 99 --timeout 45 > "$LOG/probe_main.log" 2>&1 &
sleep 42
echo "== processes at +45s"; adb shell ps -A | grep "$PKG" | awk '{print $2, $NF}'
echo "== logcat"; adb logcat -d | grep -iE "monodroid-debug|debugger-agent|has died" | cut -c1-220 | head -30
echo "== main probe hits: $(grep -c 'BREAKPOINT HIT' "$LOG/probe_main.log")"; grep -E "TIMEOUT|EXCEPTION" "$LOG/probe_main.log" | head -3
wait
adb shell "setprop debug.mono.extra ''"; adb shell am force-stop "$PKG"
