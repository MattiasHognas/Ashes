#!/usr/bin/env bash
set -euo pipefail
binary=$(realpath "${1:?compiled identity fixture binary}")
output=${2:?output directory}
mkdir -p "$output"
ulimit -s 1048576
for mode in check 1000 10000; do
    systemd-run --user --scope -q -p MemoryMax=4G -p MemorySwapMax=0 \
        /usr/bin/time -v -o "$output/$mode.time" "$binary" "$mode" \
        > "$output/$mode.stdout" 2> "$output/$mode.stderr"
    expected='fresh graph identities passed'
    if [[ "$mode" == check ]]; then
        expected='graph identity and collision tests passed'
    fi
    [[ $(cat "$output/$mode.stdout") == "$expected" ]]
    [[ $(awk '/^ashes-rc-live [0-9]+$/ {n++} END {print n+0}' "$output/$mode.stderr") == 1 ]]
    ! rg -q 'Command terminated|non-zero status' "$output/$mode.time"
done
small=$(awk '/^ashes-rc-live / {print $2}' "$output/1000.stderr")
large=$(awk '/^ashes-rc-live / {print $2}' "$output/10000.stderr")
[[ "$small" == "$large" ]]
printf 'Graph identity tests passed; fresh index live cells %s -> %s.\n' "$small" "$large"
