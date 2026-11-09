#!/bin/bash
# heads.sh <dumpdir> <head-class> : the head cells' first 24 payload bytes, for 32_ptr cells whose word0 points at a cell of that class and whose word1 is not a heap pointer
export LC_ALL=C
D=$1; want=$2; base=$((0x100000000000)); chunk=$((256*1048576))
declare -A cls
while read a s w; do cls[$a]="${s}_${w}"; done < $D/c.idx
rdn() { local off=$(( $1 - base )); local f=$D/full-0.bin; [ $off -ge $chunk ] && { off=$((off-chunk)); f=$D/full-1.bin; }; od -A n -t x1 -j $off -N $2 $f | tr -d ' \n'; }
for a in $(awk '$2==32 && $3=="ptr" {print $1}' $D/c.idx); do
  w0=$(rdn $((0x$a + 16)) 8 | sed 's/\(..\)/\1 /g' | awk '{for(i=8;i>=1;i--) printf $i}'); t0=$(printf "%x" $(( 0x$w0 - 16 )))
  [ "${cls[$t0]}" = "$want" ] || continue
  echo "$(rdn $((0x$t0 + 16)) 24) $(rdn $((0x$a + 24)) 8)"
done | sort | uniq -c
