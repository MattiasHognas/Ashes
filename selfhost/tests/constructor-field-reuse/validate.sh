#!/usr/bin/env bash
set -euo pipefail
binary=$(realpath "${1:?compiled fixture binary}")
output=${2:?output directory}
mkdir -p "$output"
ulimit -s 1048576
for mode in check old new 1000 10000; do
    systemd-run --user --scope -q -p MemoryMax=4G -p MemorySwapMax=0 \
        /usr/bin/time -v -o "$output/$mode.time" "$binary" "$mode" \
        > "$output/$mode.stdout" 2> "$output/$mode.stderr"
    case "$mode" in
        check) expected='1000 field relations ok' ;;
        old|new) expected=400000 ;;
        1000) expected=4000 ;;
        10000) expected=40000 ;;
    esac
    [[ $(cat "$output/$mode.stdout") == "$expected" ]]
    [[ $(awk '/^ashes-rc-live [0-9]+$/ {n++} END {print n+0}' "$output/$mode.stderr") == 1 ]]
    ! rg -q 'Command terminated|non-zero status' "$output/$mode.time"
done
small=$(awk '/^ashes-rc-live / {print $2}' "$output/1000.stderr")
large=$(awk '/^ashes-rc-live / {print $2}' "$output/10000.stderr")
[[ "$small" == "$large" ]]
printf 'All field relations passed; fresh index live cells %s -> %s.\n' "$small" "$large"
