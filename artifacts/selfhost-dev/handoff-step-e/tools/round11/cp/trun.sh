#!/bin/bash
# trun.sh <test.ash> [extra -E flags...] : compile with leak check (plain and compile-time poison), run capped, print output and live count
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/post-prefix-capture
cd "$R" || exit 1
t=$1; shift; b=work/cp/trun/$(basename "$t" .ash); mkdir -p work/cp/trun
for mode in plain poison; do
  p=""; [ $mode = poison ] && p="-E ASHES_RC_POISON=1"
  work/cp/cap.sh 8G $p "$@" -E ASHES_RC_LEAKCHECK=1 dotnet run -c Release --no-build --project src/Ashes.Cli -- compile "$t" -o "$b.$mode" > "$b.$mode.log" 2>&1 || { echo "$(basename $t) $mode compile fail"; continue; }
  echo "$(basename $t .ash) $mode $*: $(work/cp/cap.sh 4G timeout 60 "$b.$mode" 2>&1 | tr '\n' ' ')"
done
