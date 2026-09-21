#!/bin/bash
# usage: tinyrun.sh <stage1-binary> <src.ash> ; compiles one small file with stage 1 and prints exit
# status, elapsed time and peak RSS.
. "$(dirname "$0")/env.sh"
bin=$1; src=$2
rm -f "$J/tiny.out"
start=$(date +%s.%N)
systemd-run --user --scope -q -p MemoryMax=40G -p MemorySwapMax=0 bash -c \
    "ulimit -s 1048576; /usr/bin/time -f 'PEAK_RSS_KB=%M' '$bin' compile '$src' -o '$J/tiny.out'; echo EXIT=\$?" > "$J/tinyrun.log" 2>&1
end=$(date +%s.%N)
echo "elapsed=$(echo "$end - $start" | bc)s $(grep -E 'PEAK_RSS_KB|EXIT' "$J/tinyrun.log" | tr '\n' ' ')"
grep -vE 'PEAK_RSS_KB|EXIT' "$J/tinyrun.log" | tail -3 | cut -c1-200
[ -x "$J/tiny.out" ] && echo "output: $("$J/tiny.out" 2>&1 | head -2)"
