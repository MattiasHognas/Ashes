#!/bin/bash
# usage: stage2.sh <stage1-binary> [tag] ; the real stage-2 build: stage 1 compiling the whole self-hosted CLI
# project, under the 40G cap. Prints elapsed time, peak RSS, exit status and the first diagnostic; a
# produced stage-2 compiler is copied to the job dir as s2-<tag>. RSS is also sampled every 30 s.
. "$(dirname "$0")/env.sh"
bin=$1; tag=${2:-run}
cd "$R" || exit 1
rm -rf selfhost/packages/cli/out
start=$(date +%s)
( while sleep 30; do
    pid=$(pgrep -n -f "^$bin compile --project selfhost/packages/cli"); [ -z "$pid" ] && continue
    echo "t=$(( $(date +%s) - start ))s rss=$(awk '/VmRSS/ {printf "%.0f", $2/1024}' "/proc/$pid/status" 2>/dev/null)MB"
  done > "$T/stage2-$tag.rss" 2>/dev/null ) &
sampler=$!
systemd-run --user --scope -q -p MemoryMax=40G -p MemorySwapMax=0 bash -c \
    "ulimit -s 1048576; /usr/bin/time -f 'PEAK_RSS_KB=%M' '$bin' compile --project selfhost/packages/cli/ashes.json; echo EXIT=\$?" > "$T/stage2-$tag.log" 2>&1
kill "$sampler" 2>/dev/null
echo "elapsed=$(( $(date +%s) - start ))s $(grep -E 'PEAK_RSS_KB|EXIT' "$T/stage2-$tag.log" | tr '\n' ' ')"
grep -vE 'PEAK_RSS_KB|EXIT' "$T/stage2-$tag.log" | head -4 | cut -c1-240
tail -3 "$T/stage2-$tag.rss"
out=$(ls selfhost/packages/cli/out/ 2>/dev/null | head -1)
[ -n "$out" ] && cp "selfhost/packages/cli/out/$out" "$J/s2-$tag" && ls -la "$J/s2-$tag"
