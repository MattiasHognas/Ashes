#!/bin/bash
# usage: parseloop.sh [-b] <source.ash> <rounds>... ; the self-hosted lexer and parser run <rounds> times over one
# source (projects/parseloop): exit status, output, time and peak memory per round count. A peak that grows with
# the rounds is a leak in the frontend, measured on its real code. -b rebuilds the tool. The source's import and
# export header is stripped first, since `parseProgram` stops at one.
. "$(dirname "$0")/env.sh"
materialize_project parseloop
cd "$R" || exit 1
if [ "$1" = "-b" ]; then
    shift
    rm -rf "$J/parseloop/out"
    dotnet run -c Release --no-build --project src/Ashes.Cli -- compile ${DEBUG:+--debug} --project "$J/parseloop/ashes.json" > "$T/parseloop-build.log" 2>&1
    echo "parseloop build exit=$? $(grep -E 'ASH[0-9]+|rror' "$T/parseloop-build.log" | head -3 | cut -c1-240)"
fi
BIN=$(ls "$J/parseloop/out/" 2>/dev/null | head -1)
[ -z "$BIN" ] && { echo "no parseloop binary; run with -b"; exit 1; }
awk 'BEGIN { skip = 0 } /^export \($/ { skip = 1; next } skip && /^\)$/ { skip = 0; next } skip { next } /^import / { next } { print }' \
    "$1" > "$J/parseloop/body.ash"
shift
for n in "$@"; do
    x=$(head -c "$n" /dev/zero | tr '\0' 'x')
    systemd-run --user --scope -q -p MemoryMax=8G -p MemorySwapMax=0 /usr/bin/time -f '%e %M' -o "$J/parseloop/time" \
        "$J/parseloop/out/$BIN" "$J/parseloop/body.ash" "$x" > "$J/parseloop/out.txt" 2>&1
    code=$?
    read -r secs kb < <(tail -1 "$J/parseloop/time")
    printf 'rounds=%-5s exit=%-3s %6ss %8s MB  out=%s\n' "$n" "$code" "$secs" "$(awk -v k="${kb:-0}" 'BEGIN{printf "%.1f", k/1024}')" "$(head -c 80 "$J/parseloop/out.txt")"
done
