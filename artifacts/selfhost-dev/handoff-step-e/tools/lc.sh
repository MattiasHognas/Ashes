#!/bin/bash
# usage: lc.sh <work-name> [r1] [r2] ; the leak check of a ROUNDS template: live RC cells at exit at r1 and r2 rounds
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
W=$R/artifacts/selfhost-dev/handoff-step-e/work
n=$1; a=${2:-1}; b=${3:-2}
for rounds in "$a" "$b"; do
    sed "s/ROUNDS/$rounds/" "$W/$n.ash" > "$W/lc-$rounds.ash"
    ASHES_RC_LEAKCHECK=1 dotnet run -c Release --no-build --project "$R/src/Ashes.Cli" -- compile "$W/lc-$rounds.ash" -o "$W/lc-$rounds.bin" > "$W/lc.compile" 2>&1 || { echo "compile failed"; tail -3 "$W/lc.compile"; exit 1; }
    "$W/lc-$rounds.bin" > /dev/null 2> "$W/lc-$rounds.err"
    echo "$n rounds=$rounds exit=$? $(grep ashes-rc-live "$W/lc-$rounds.err")"
done
