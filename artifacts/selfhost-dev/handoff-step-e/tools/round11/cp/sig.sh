#!/bin/bash
# sig.sh <dumpdir> : for every live 32_ptr cell, "target0-class / word1-kind" where target0 is the class of the cell word0 points at
export LC_ALL=C
D=$1; base=$((0x100000000000)); chunk=$((256*1048576))
awk '{print $2, $3, $4}' $D/cells.txt > $D/c.idx
declare -A cls
while read a s w; do cls[$a]="${s}_${w}"; done < $D/c.idx
rd() { local off=$(( $1 - base )); local f=$D/full-0.bin; [ $off -ge $chunk ] && { off=$((off-chunk)); f=$D/full-1.bin; }; od -A n -t x8 -j $off -N 8 $f | tr -d ' '; }
for a in $(awk '$2==32 && $3=="ptr" {print $1}' $D/c.idx); do
  w0=$(rd $((0x$a + 16))); w1=$(rd $((0x$a + 24)))
  t0=$(printf "%x" $(( 0x$w0 - 16 ))); c0=${cls[$t0]:-?}
  if [ $(( 0x$w1 )) -ge $base ] && [ $(( 0x$w1 )) -lt $((base + 2*chunk)) ]; then t1=$(printf "%x" $(( 0x$w1 - 16 ))); k1="ptr:${cls[$t1]:-?}"; else k1="val"; fi
  echo "$c0 | $k1"
done | sort | uniq -c | awk '{c=$1; $1=""; print substr($0,2) "\t" c}' | sort
