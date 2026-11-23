#!/usr/bin/env bash
# Runs the measurement driver in one mode over every test program, one at a time, and writes one
# TSV row per program that lowered (with its name in front) plus the programs that did not.
# Usage: sweep.sh <measure binary> <typed|legacy> <out dir> [test dir]
set -u
measure=$1
mode=$2
out=$3
tests=${4:-tests}
mkdir -p "$out"
ulimit -s 1048576
ulimit -c 0
: > "$out/rows.tsv"
: > "$out/failed.txt"
header=
for file in "$tests"/*.ash; do
    name=$(basename "$file" .ash)
    if grep -q '^// expect-compile-error' "$file"; then
        continue
    fi
    systemd-run --user --scope -q -p MemoryMax=4G -p MemorySwapMax=0 timeout 120 \
        "$measure" "$file" "$mode" --goal-details > "$out/$name.tsv" 2> "$out/$name.err"
    code=$?
    if [ "$code" = 0 ] && [ "$(wc -l < "$out/$name.tsv")" = 2 ]; then
        if [ -z "$header" ]; then
            header=$(head -n 1 "$out/$name.tsv")
            printf 'program\t%s\n' "$header" > "$out/header.tsv"
        fi
        printf '%s\t%s\n' "$name" "$(tail -n 1 "$out/$name.tsv")" >> "$out/rows.tsv"
        rm -f "$out/$name.tsv"
        if [ "$(tail -n 1 "$out/rows.tsv" | cut -f 7)" = no ] && [ "$(tail -n 1 "$out/rows.tsv" | cut -f 11)" = 0 ]; then
            rm -f "$out/$name.err"
        fi
    else
        printf '%s\texit=%s\t%s\n' "$name" "$code" "$(tail -n 1 "$out/$name.err")" >> "$out/failed.txt"
    fi
done
# rows.tsv fields: 7 replayed, 8 pending_traits, 9 equality_goals, 11 first_deferred_operators.
awk -F '\t' '{ programs++; if ($7 == "yes") replays++; pending += $8; equality += $9; deferred += $11 } END { printf "programs=%d replays=%d pending_traits=%d equality_goals=%d first_deferred_operators=%d\n", programs, replays, pending, equality, deferred }' "$out/rows.tsv" | tee "$out/summary.txt"
