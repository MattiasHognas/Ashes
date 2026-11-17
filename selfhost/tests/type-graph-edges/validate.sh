#!/usr/bin/env bash
set -euo pipefail
binary=$(realpath "${1:?compiled edge fixture}")
out=${2:?evidence directory}
mkdir -p "$out"
ulimit -s 1048576
for mode in check 1000 10000; do
    systemd-run --user --scope -q -p MemoryMax=4G -p MemorySwapMax=0 \
        /usr/bin/time -v -o "$out/$mode.time" "$binary" "$mode" > "$out/$mode.stdout" 2> "$out/$mode.stderr"
    expected='fresh graph edges passed'
    if [[ $mode == check ]]; then expected='graph edge tests passed'; fi
    [[ $(cat "$out/$mode.stdout") == "$expected" ]]
    [[ $(awk '/^ashes-rc-live [0-9]+$/ {n++} END {print n+0}' "$out/$mode.stderr") == 1 ]]
    ! rg -q 'Command terminated|non-zero status' "$out/$mode.time"
done
small=$(awk '/^ashes-rc-live / {print $2}' "$out/1000.stderr")
large=$(awk '/^ashes-rc-live / {print $2}' "$out/10000.stderr")
[[ $small == "$large" ]]
printf 'Graph edge relations passed; fresh live cells %s -> %s.\n' "$small" "$large"
