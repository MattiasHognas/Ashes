#!/bin/bash
# waits until fewer than two heavy jobs run (distinct working directories of `compile --project` and
# C# test processes) and at least 25 GB are available, then runs the command
while true; do
  n=$(ps -eo pid,args | grep -E "compile --project|Ashes.Tests.dll" | grep -v -E "grep|waitslot" | awk '{print $1}' | while read p; do readlink /proc/$p/cwd; done | sort -u | wc -l)
  f=$(free -g | awk '/^Mem:/ {print $7}')
  [ "$n" -lt 2 ] && [ "$f" -ge 25 ] && break
  sleep 20
done
exec "$@"
