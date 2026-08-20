#!/usr/bin/env bash
# Experiment B behind ANDROID_ATTACH_NOTES.md "Multi-process apps": port rotation.
# Starts the app with the agent on port P0, rotates debug.mono.extra to P0+1 as soon as the
# main process has read it, then debugs main (P0) and the first helper process (P0+1)
# concurrently with two SdbProbe instances.
#
# Usage:
#   PKG=<package> MAINBP=<abs file>:<line> HELPERBP=<abs file>:<line> [PORT=10000] \
#   [PROBE=./DevTools/SdbProbe/bin/Debug/net10.0/SdbProbe.exe] bash multiprocess-port-rotation.sh <logdir>
# Needs: app deployed, adb on PATH, SdbProbe built. Leaves the property cleared and the app stopped.
set -u
# Never rely on the default adb device: other emulators/devices may be attached. adb honors ANDROID_SERIAL.
: "${ANDROID_SERIAL:?set ANDROID_SERIAL to the target device serial}"
PKG=${PKG:?package}; MAINBP=${MAINBP:?file:line}; HELPERBP=${HELPERBP:?file:line}
PORT=${PORT:-10000}; PORT2=$((PORT + 1))
PROBE=${PROBE:-./DevTools/SdbProbe/bin/Debug/net10.0/SdbProbe.exe}
LOG=${1:?logdir}

ACT=$(adb shell cmd package resolve-activity --brief "$PKG" | tail -1 | tr -d '\r')
T=$(( $(adb shell date +%s | tr -d '\r') + 300 ))
adb shell am force-stop "$PKG"
adb shell setprop debug.mono.extra "debug=127.0.0.1:$PORT,timeout=$T,loglevel=0,server=y"
adb forward tcp:$PORT tcp:$PORT >/dev/null; adb forward tcp:$PORT2 tcp:$PORT2 >/dev/null
adb logcat -c
adb shell am start -n "$ACT" >/dev/null
for i in $(seq 1 40); do adb logcat -d | grep -q "monodroid-debug: Trying to initialize" && break; sleep 0.25; done
echo "== main read prop after ~$((i*250))ms; rotating to $PORT2"
adb shell setprop debug.mono.extra "debug=127.0.0.1:$PORT2,timeout=$T,loglevel=0,server=y"
"$PROBE" --port $PORT --file "${MAINBP%:*}" --line "${MAINBP##*:}" --hits 99 --timeout 30 --app main > "$LOG/probe_main.log" 2>&1 &
sleep 4
echo "== processes at +4s"; adb shell ps -A | grep "$PKG" | awk '{print $2, $NF}'
"$PROBE" --port $PORT2 --file "${HELPERBP%:*}" --line "${HELPERBP##*:}" --hits 2 --timeout 30 --app helper > "$LOG/probe_helper.log" 2>&1
echo "== helper probe"; grep -E "TargetReady|BREAKPOINT HIT|local |TIMEOUT|EXCEPTION|done" "$LOG/probe_helper.log" | cut -c1-160 | head -12
wait
echo "== main probe hits: $(grep -c 'BREAKPOINT HIT' "$LOG/probe_main.log")"; grep -E "TIMEOUT|EXCEPTION" "$LOG/probe_main.log" | head -3
echo "== logcat"; adb logcat -d | grep -iE "monodroid-debug|debugger-agent|has died" | cut -c1-200 | head -12
echo "== processes at end"; adb shell ps -A | grep "$PKG" | awk '{print $2, $NF}'
adb shell "setprop debug.mono.extra ''"; adb shell am force-stop "$PKG"
