#!/bin/bash
# usage: plateau.sh <template.ash> <rounds>... ; compiles the template with the word ROUNDS replaced by each value,
# using this checkout's built compiler, runs it under an 8G cap and prints exit status, output, time and peak
# memory. A peak that stays flat as the rounds grow means nothing leaks per round. TOGGLE=NAME=1 compiles under one
# environment switch.
. "$(dirname "$0")/env.sh"
t=$1; shift
E=$J/plateau
mkdir -p "$E"
cd "$R" || exit 1
for n in "$@"; do
    sed "s/ROUNDS/$n/" "$t" > "$E/run.ash"
    rm -f "$E/run.bin"
    env "${TOGGLE:-_UNUSED=1}" dotnet run -c Release --no-build --project src/Ashes.Cli -- compile "$E/run.ash" -o "$E/run.bin" > "$E/run.compile" 2>&1
    built=$?
    systemd-run --user --scope -q -p MemoryMax=8G -p MemorySwapMax=0 /usr/bin/time -f '%e %M' -o "$E/run.time" "$E/run.bin" > "$E/run.out" 2> /dev/null
    code=$?
    read -r secs kb < <(tail -1 "$E/run.time")
    printf 'rounds=%-7s compile=%s exit=%-3s %7ss %9s MB  out=%s\n' "$n" "$built" "$code" "$secs" "$(awk -v k="${kb:-0}" 'BEGIN{printf "%.1f", k/1024}')" "$(head -1 "$E/run.out" | cut -c1-40)"
done
