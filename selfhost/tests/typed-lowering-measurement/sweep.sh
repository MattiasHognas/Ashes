#!/usr/bin/env bash
# Runs the measurement driver over every test program, one at a time, and writes one TSV row per
# program that lowered (with its name in front) plus the programs that did not; the step-8 shadow
# counts and sites and the hash of each program's lowered IR beside them.
# Usage: sweep.sh <measure binary> typed <out dir> [test dir]
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
: > "$out/rejected.txt"
: > "$out/shadow.tsv"
: > "$out/shadow-sites.tsv"
: > "$out/ir.tsv"
mkdir -p "$out/sources"
header=
for file in "$tests"/*.ash; do
    name=$(basename "$file" .ash)
    if grep -q '^// expect-compile-error' "$file"; then
        continue
    fi
    # The test runner substitutes a loopback port for __TCP_PORT__; any number type-checks the same.
    source="$out/sources/$name.ash"
    sed "s/__TCP_PORT__/40000/g" "$file" > "$source"
    systemd-run --user --scope -q -p MemoryMax=4G -p MemorySwapMax=0 timeout 240 \
        "$measure" "$source" "$mode" --goal-details --shadow-details --ir-hash > "$out/$name.tsv" 2> "$out/$name.err"
    code=$?
    if [ "$code" = 0 ] && [ "$(wc -l < "$out/$name.tsv")" = 2 ]; then
        if [ -z "$header" ]; then
            header=$(head -n 1 "$out/$name.tsv")
            printf 'program\t%s\n' "$header" > "$out/header.tsv"
        fi
        printf '%s\t%s\n' "$name" "$(tail -n 1 "$out/$name.tsv")" >> "$out/rows.tsv"
        rm -f "$out/$name.tsv"
        # One shadow row per program: rule, agree, differ and unrecorded for each of the twelve rules.
        awk -v program="$name" '/^shadow rule=/ { split($0, f, /[ =]/); printf "%s\t%s\t%s\t%s\t%s\n", program, f[3], f[5], f[7], f[9] }' "$out/$name.err" >> "$out/shadow.tsv"
        awk -v program="$name" -F '\t' '/^shadow-site\t/ { printf "%s\t%s\t%s\t%s\t%s\n", program, $2, $3, $4, $5 }' "$out/$name.err" >> "$out/shadow-sites.tsv"
        printf '%s\t%s\n' "$name" "$(grep '^ir_hash=' "$out/$name.err" | head -n 1)" >> "$out/ir.tsv"
        if [ "$(tail -n 1 "$out/rows.tsv" | cut -f 7)" = no ] && [ "$(tail -n 1 "$out/rows.tsv" | cut -f 11)" = 0 ]; then
            rm -f "$out/$name.err"
        fi
    else
        if cat "$out/$name.tsv" "$out/$name.err" | grep -q "infer-error"; then
            # A program the inference rejects (the `_fails` diagnostics fixtures) is not lowered.
            printf '%s\t%s\n' "$name" "$(cat "$out/$name.tsv" "$out/$name.err" | grep "infer-error" | head -c 200)" >> "$out/rejected.txt"
        else
            printf '%s\texit=%s\t%s\n' "$name" "$code" "$(tail -n 1 "$out/$name.err")" >> "$out/failed.txt"
        fi
    fi
done
# rows.tsv fields: 7 replayed, 8 pending_traits, 9 equality_goals, 11 first_deferred_operators.
awk -F '\t' '{ programs++; if ($7 == "yes") replays++; pending += $8; equality += $9; deferred += $11 } END { printf "programs=%d replays=%d pending_traits=%d equality_goals=%d first_deferred_operators=%d\n", programs, replays, pending, equality, deferred }' "$out/rows.tsv" | tee "$out/summary.txt"
# Per rule: agreeing sites, differing sites, unrecorded sites, and programs with a differing site.
awk -F '\t' '{ agree[$2] += $3; differ[$2] += $4; unrecorded[$2] += $5; if ($4 > 0) programs[$2]++ } END { for (rule = 1; rule <= 12; rule++) printf "shadow rule=%d agree=%d differ=%d unrecorded=%d programs_differing=%d\n", rule, agree[rule], differ[rule], unrecorded[rule], programs[rule] }' "$out/shadow.tsv" | tee -a "$out/summary.txt"
