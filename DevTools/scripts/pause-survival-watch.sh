#!/usr/bin/env bash
# Samples an app package (pids + ANR/watchdog/MQTT/death logcat lines) while the debugger holds it
# paused, to answer "does a long pause break this app?". Usage: edit S/PKG below, then bash <script>.
# Results of the the reference application run: ANDROID_ATTACH_NOTES.md, the reference application section (U6).
S=emulator-5554; PKG=App.Droid; OUT=u6_watch.log
echo "== U6 watch start $(date +%H:%M:%S)" > $OUT
adb -s $S logcat -c
for i in $(seq 1 9); do
  sleep 30
  PIDS=$(adb -s $S shell "ps -A | grep -i App.Droid" | awk '{print $2":"$NF}' | tr '\n' ' ')
  echo "[$(date +%H:%M:%S)] t+$((i*30))s pids: $PIDS" >> $OUT
  adb -s $S logcat -d | grep -iE "ANR |Application Not Responding|not responding|has died|Force finishing|watchdog|WatchDog|riavviati forzatamente|bugreport|BugReport|mqtt|Mqtt|lowmemorykiller" | tail -6 >> $OUT
done
echo "== watch end $(date +%H:%M:%S)" >> $OUT
