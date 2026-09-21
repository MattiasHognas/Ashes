#!/bin/bash
# usage: tinyrootdiff.sh <stage1-binary> <small.ash> <larger.ash> ; tallies the leaked list-head roots at
# exit by shape (length, element size, element tag) for two inputs and prints the shapes whose count
# differs, with the difference in roots and in cells.
. "$(dirname "$0")/env.sh"
bash "$HERE/mkrcroots.sh" > /dev/null || exit 1
for which in 1 2; do
    src=$2; [ $which = 2 ] && src=$3
    CENSUS_LINES=1 STATE_LINES=1 bash "$HERE/tinycensus.sh" "$1" "$src" > /dev/null
    RCROOTS_TALLY=1 "$T/rcroots" "$T/tiny-dump" "$T/irkinds.txt" | grep "^T " | cut -d' ' -f2-4 | sort | uniq -c | awk '{print $2" "$3" "$4" "$1}' | sort > "$T/rootdiff-$which.txt"
done
join -a1 -a2 -e0 -o 0,1.2,2.2 <(awk '{print $1"_"$2"_"$3" "$4}' "$T/rootdiff-1.txt" | sort) <(awk '{print $1"_"$2"_"$3" "$4}' "$T/rootdiff-2.txt" | sort) \
  | awk '$2 != $3 { split($1, p, "_"); sub(/len=/, "", p[1]); d=$3-$2; printf "%+5d roots  %+7d cells  %s\n", d, d*p[1], $1 }' | sort -t' ' -k4 -n -r | sort -k3 -n -r | head -${4:-25}
