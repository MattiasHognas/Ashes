#!/bin/bash
# usage: exitswitch.sh <tag> [ENV=val] ; leak-counting stage 1 at HEAD with the switch, then the loops10/11 exit census
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
W=$R/artifacts/selfhost-dev/handoff-step-e/work
H=/home/mattias/.claude/jobs/heavy2.sh
cd "$R" || exit 1
export UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
bash $H systemd-run --user --scope -q -p MemoryMax=16G -p MemorySwapMax=0 bash scripts/selfhost-dev/quickstage1.sh "$1" ASHES_RC_LEAKCHECK=1 "$2"
for n in 10 11; do
    bash $H systemd-run --user --scope -q -p MemoryMax=8G -p MemorySwapMax=0 \
        bash -c "ulimit -s 1048576; timeout 600 '$R/artifacts/selfhost-dev/s1-$1' compile '$R/artifacts/selfhost-dev/tiny/loops$n.ash' -o '$W/xloops$n.out'" > "$W/xloops-$1-$n.log" 2>&1
done
a=$(grep -a "ashes-rc-live" "$W/xloops-$1-10.log" | tail -1 | awk '{print $2}')
b=$(grep -a "ashes-rc-live" "$W/xloops-$1-11.log" | tail -1 | awk '{print $2}')
echo "RESULT $1 live $a/$b per-fn $(( b - a ))"
