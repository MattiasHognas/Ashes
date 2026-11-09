#!/bin/bash
# usage: repros.sh <stage1-binary> <list-file>; each list line names a reproducer .ash with a ROUNDS placeholder
# (copied under work/repros/). Prints ashes-rc-live at 1, 2 and 10 rounds for stage 0 and stage 1, plain and poisoned.
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
S1=$1; L=$2
D=$R/artifacts/selfhost-dev/handoff-step-e/work/repros-out; mkdir -p $D
cd $R || exit 1
grep -vE '^\s*(#|$)' "$L" | while read -r src; do
  b=$(basename $src .ash)
  for r in 1 2 10; do
    sed "s/ROUNDS/$r/" $src > $D/${b}_$r.ash
    ASHES_RC_LEAKCHECK=1 dotnet run -c Release --no-build --project src/Ashes.Cli -- compile -O2 "$D/${b}_$r.ash" -o "$D/${b}_$r.s0" > $D/${b}_$r.s0log 2>&1 || echo "$b r=$r s0 COMPILE FAIL"
    (ulimit -s 1048576; ASHES_RC_LEAKCHECK=1 systemd-run --user --scope -q -p MemoryMax=8G -p MemorySwapMax=0 timeout 600 $S1 compile "$D/${b}_$r.ash" -o "$D/${b}_$r.s1" > $D/${b}_$r.s1log 2>&1) || echo "$b r=$r s1 COMPILE FAIL"
    for s in s0 s1; do
      [ -f $D/${b}_$r.$s ] || continue
      for p in plain poison; do
        e=""; [ $p = poison ] && e="ASHES_RC_POISON=1"
        systemd-run --user --scope -q -p MemoryMax=4G -p MemorySwapMax=0 timeout 120 env ASHES_RC_LEAKCHECK=1 $e "$D/${b}_$r.$s" > $D/${b}_$r.$s.$p.out 2> $D/${b}_$r.$s.$p.err; code=$?
        out=$(grep -a ashes-rc-live $D/${b}_$r.$s.$p.err | tr "\n" " ")
        same=same; cmp -s $D/${b}_$r.$s.$p.out $D/${b}_$r.s0.plain.out || same=OUTPUT-DIFFERS-FROM-S0-PLAIN
        echo "$b r=$r $s $p exit=$code $out $same"
      done
    done
  done
done
