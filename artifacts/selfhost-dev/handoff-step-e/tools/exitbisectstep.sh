#!/bin/bash
# git bisect run step: builds the leak-counting stage 1 at HEAD and measures loops10/loops11 rc-live at exit; bad when the per-function difference exceeds 2000
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
W=$R/artifacts/selfhost-dev/handoff-step-e/work
H=/home/mattias/.claude/jobs/heavy2.sh
cd "$R" || exit 125
export UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
h=$(git rev-parse --short HEAD)
echo "-- $(date +%H:%M:%S) step $h"
bash $H systemd-run --user --scope -q -p MemoryMax=16G -p MemorySwapMax=0 bash scripts/selfhost-dev/quickstage1.sh xlc$h ASHES_RC_LEAKCHECK=1
B=$R/artifacts/selfhost-dev/s1-xlc$h
[ -x "$B" ] || exit 125
for n in 10 11; do
    bash $H systemd-run --user --scope -q -p MemoryMax=8G -p MemorySwapMax=0 \
        bash -c "ulimit -s 1048576; timeout 600 '$B' compile '$R/artifacts/selfhost-dev/tiny/loops$n.ash' -o '$W/xloops$n.out'" > "$W/xloops-$h-$n.log" 2>&1
done
a=$(grep -a "ashes-rc-live" "$W/xloops-$h-10.log" | tail -1 | awk '{print $2}')
b=$(grep -a "ashes-rc-live" "$W/xloops-$h-11.log" | tail -1 | awk '{print $2}')
[ -z "$a" ] || [ -z "$b" ] && exit 125
d=$(( b - a ))
echo "$h live $a/$b per-fn $d"
[ $d -gt 2000 ] && exit 1
exit 0
