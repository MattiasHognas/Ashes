#!/bin/bash
# usage: s1run.sh <tpl> <s1-binary> [rounds...] : stage-1 leak counts (leak check on, not poisoned)
cd "$(dirname "$0")/../.." || exit 1
t=$1; S1=$2; shift 2; rs=${*:-1 2}
for r in $rs; do
  sed "s/ROUNDS/$r/" work/cp/$t.tpl > work/cp/${t}_$r.ash
  bash -c "ulimit -s 1048576; ASHES_RC_LEAKCHECK=1 systemd-run --user --scope -q -p MemoryMax=4G -p MemorySwapMax=0 timeout 300 $S1 compile work/cp/${t}_$r.ash -o work/cp/${t}_$r.s1" > work/cp/${t}_$r.s1log 2>&1 || { echo "$t s1 compile fail: $(grep -v ashes-rc-live work/cp/${t}_$r.s1log | head -1)"; continue; }
  echo "$t rounds=$r s1 (not poisoned): $(systemd-run --user --scope -q -p MemoryMax=4G -p MemorySwapMax=0 timeout 120 work/cp/${t}_$r.s1 2>&1 | tr '\n' ' ')"
done
