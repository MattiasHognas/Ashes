#!/bin/bash
# usage: s0.sh <tpl> [rounds...] ; stage-0 leak counts, plain and poisoned
cd "$(dirname "$0")/../.." || exit 1
t=$1; shift; rs=${*:-1 2 10}
for r in $rs; do
  sed "s/ROUNDS/$r/" work/cp/$t.tpl > work/cp/${t}_$r.ash
  ASHES_RC_LEAKCHECK=1 dotnet run -c Release --no-build --project src/Ashes.Cli -- compile work/cp/${t}_$r.ash -o work/cp/${t}_$r.s0 > work/cp/${t}_$r.s0log 2>&1 || { echo "$t s0 compile fail"; tail -3 work/cp/${t}_$r.s0log; continue; }
  out=$(systemd-run --user --scope -q -p MemoryMax=4G -p MemorySwapMax=0 timeout 120 work/cp/${t}_$r.s0 2>&1 | tr '\n' ' ')
  pout=$(ASHES_RC_POISON=1 systemd-run --user --scope -q -p MemoryMax=4G -p MemorySwapMax=0 timeout 120 work/cp/${t}_$r.s0 2>&1 | tr '\n' ' ')
  echo "$t rounds=$r plain: $out | poison: $pout"
done
