#!/bin/bash
# usage: plateauwith.sh <cli-label> <template.ash> <rounds>... ; plateau.sh with a published compiler ($J/cli-<label>,
# from publishcli.sh or publishref.sh) instead of this checkout's build.
. "$(dirname "$0")/env.sh"
label=$1; t=$2; shift 2
E=$J/plateau-$label
mkdir -p "$E"
for n in "$@"; do
    sed "s/ROUNDS/$n/" "$t" > "$E/run.ash"
    rm -f "$E/run.bin"
    "$J/cli-$label/ashes" compile "$E/run.ash" -o "$E/run.bin" > "$E/run.compile" 2>&1
    built=$?
    systemd-run --user --scope -q -p MemoryMax=8G -p MemorySwapMax=0 /usr/bin/time -f '%e %M' -o "$E/run.time" "$E/run.bin" > "$E/run.out" 2> /dev/null
    code=$?
    read -r secs kb < <(tail -1 "$E/run.time")
    printf '%s rounds=%-7s compile=%s exit=%-3s %7ss %9s MB  out=%s\n' "$label" "$n" "$built" "$code" "$secs" "$(awk -v k="${kb:-0}" 'BEGIN{printf "%.1f", k/1024}')" "$(head -1 "$E/run.out" | cut -c1-40)"
done
