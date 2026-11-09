#!/bin/bash
# up.sh <n> <hdr-addr> : referrer chain of a cell up to a root, one line per step (first referrer only)
g=g-$1.txt; x=$2
for i in $(seq 1 12); do
  p=$(printf '%x' $((0x$x+16)))
  r=$(grep -m1 -- "-> $p\$" $g) || { echo "ROOT $x"; break; }
  echo "$r" | awk '{print $3,$4,$5,$6}'
  x=$(echo "$r" | awk '{print $2}')
done
