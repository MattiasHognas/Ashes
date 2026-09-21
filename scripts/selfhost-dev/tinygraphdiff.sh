#!/bin/bash
# usage: tinygraphdiff.sh <stage1-binary> <small.ash> <larger.ash> ; exclusive-attribution root classes
# (rcgraph2) for both inputs, showing root count and kilobytes kept per class and the difference.
. "$(dirname "$0")/env.sh"
for which in 1 2; do
    src=$2; [ $which = 2 ] && src=$3
    CENSUS_LINES=1 STATE_LINES=1 bash "$HERE/tinycensus.sh" "$1" "$src" > /dev/null
    "$T/rcgraph2" "$T/tiny-dump" 2>/dev/null > "$T/graphdiff-$which.txt"
    head -2 "$T/graphdiff-$which.txt" | cut -c1-160
done
echo "== root classes by bytes held (roots lines), larger input minus smaller"
grep "^roots size" "$T/graphdiff-1.txt" | awk '{print $3"_"$4"_"$5, $6, $9}' | sort > "$T/gd1.txt"
grep "^roots size" "$T/graphdiff-2.txt" | awk '{print $3"_"$4"_"$5, $6, $9}' | sort > "$T/gd2.txt"
LC_ALL=C join -a1 -a2 -e0 -o 0,1.2,2.2,1.3,2.3 <(LC_ALL=C sort "$T/gd1.txt") <(LC_ALL=C sort "$T/gd2.txt") \
  | awk '{ dr=$3-$2; dm=$5-$4; if (dr != 0 || dm != 0) printf "%+6d roots %+8.3f MB  %s\n", dr, dm, $1 }' | sort -k3 -n -r | head -${4:-12}
