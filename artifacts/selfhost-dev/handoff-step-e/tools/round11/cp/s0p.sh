#!/bin/bash
# usage: s0p.sh <tpl-path> [rounds...] : stage-0 leak counts, plain and with ASHES_RC_POISON=1 set at COMPILE time (read by codegen), all capped
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/post-prefix-capture
cd "$R" || exit 1
tpl=$1; shift; rs=${*:-1 2}; t=$(basename "$tpl" .tpl); d=$(dirname "$tpl")
for r in $rs; do
  sed "s/ROUNDS/$r/" "$tpl" > "$d/${t}_$r.ash"
  for mode in plain poison; do
    p=""; [ $mode = poison ] && p="-E ASHES_RC_POISON=1"
    work/cp/cap.sh 8G $p -E ASHES_RC_LEAKCHECK=1 dotnet run -c Release --no-build --project src/Ashes.Cli -- compile "$d/${t}_$r.ash" -o "$d/${t}_$r.$mode" > "$d/${t}_$r.$mode.log" 2>&1 || { echo "$t $mode compile fail"; continue; }
    out=$(work/cp/cap.sh 4G timeout 120 "$d/${t}_$r.$mode" 2>&1 | tr '\n' ' '); echo "$t rounds=$r $mode(compile-time): $out exit=$?"
  done
done
