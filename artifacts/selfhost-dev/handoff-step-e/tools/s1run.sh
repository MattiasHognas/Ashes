#!/bin/bash
# usage: s1run.sh <stage1-bin> <work-name> <rounds> ; compiles work/<name>.ash (ROUNDS replaced) with a stage 1 and runs it
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
W=$R/artifacts/selfhost-dev/handoff-step-e/work
bin=$1; n=$2; rounds=$3
sed "s/ROUNDS/$rounds/" "$W/$n.ash" > "$W/s1run.ash"
rm -f "$W/s1run.bin"
bash -c "ulimit -s 1048576; '$bin' compile '$W/s1run.ash' -o '$W/s1run.bin'" > "$W/s1run.log" 2>&1
echo "compile=$?"
tail -3 "$W/s1run.log"
/usr/bin/time -f '%e s %M KB' "$W/s1run.bin"
echo "exit=$?"
