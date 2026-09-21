#!/bin/bash
# usage: optloop.sh [-b] <source.ash> <rounds>... ; the self-hosted optimizer run <rounds> times over one lowered
# program (projects/optloop), under a memory cap: exit status, output, time and peak memory per round count. A
# peak that grows with the rounds is a leak inside the optimizer, measured on its real code. -b rebuilds the tool.
# CENSUS=<rounds> instead dumps the reference-counted heap at exit for that round count (for tinyrootshape.sh and
# tinypeek.sh, which read the same dump directory).
. "$(dirname "$0")/env.sh"
materialize_project optloop
cd "$R" || exit 1
if [ "$1" = "-b" ]; then
    shift
    rm -rf "$J/optloop/out"
    dotnet run -c Release --no-build --project src/Ashes.Cli -- compile ${DEBUG:+--debug} --project "$J/optloop/ashes.json" > "$T/optloop-build.log" 2>&1
    echo "optloop build exit=$? $(grep -E 'ASH[0-9]+|rror' "$T/optloop-build.log" | head -3 | cut -c1-240)"
fi
BIN=$(ls "$J/optloop/out/" 2>/dev/null | head -1)
[ -z "$BIN" ] && { echo "no optloop binary; run with -b"; exit 1; }
src=$(readlink -f "$1"); shift
unary() { head -c "$1" /dev/zero | tr '\0' 'x'; }
if [ -n "$WATCH" ]; then
    # WATCH=<cell-hex> ROUNDS_WATCHED=<n>: every write to one cell's count word, with the top frames (build with
    # DEBUG=1 -b first; the census that named the cell must come from the same binary and round count).
    {
        echo "set pagination off"
        echo "set confirm off"
        echo "watch *(long *)0x$WATCH"
        echo "run"
        echo "bt ${FRAMES:-6}"
        for _ in $(seq 1 "${STOPS:-40}"); do echo "continue"; echo "bt ${FRAMES:-6}"; done
        echo "kill"
    } > "$T/optloop-watch.gdb"
    bash -c "ulimit -s 1048576; timeout 900 gdb -q -batch -x '$T/optloop-watch.gdb' --args '$J/optloop/out/$BIN' '$src' '$(unary "${ROUNDS_WATCHED:-2}")'" > "$T/optloop-watch.log" 2>&1
    awk '
    /^Old value/ { old=$4 }
    /^New value/ { if (line != "") print line; nv=$4; line=sprintf("%s -> %s :", (length(old) > 6 ? "PTR" : old), (length(nv) > 6 ? "PTR" : nv)) }
    /^#[0-9]+ / { fn=$2; if ($2 ~ /^0x/) fn=$4; ln=$NF; sub(/.*\//, "", ln); line=line " " fn "@" ln }
    END { if (line != "") print line }' "$T/optloop-watch.log" | tail -${TAIL:-8}
    exit 0
fi
if [ -n "$CENSUS" ]; then
    D=$T/tiny-dump
    rm -rf "$D"; mkdir -p "$D"
    base=$((0x100000000000)); chunk=$((256 * 1048576))
    {
        echo "set pagination off"
        echo "catch syscall exit exit_group"
        echo "run"
        echo "dump binary memory $D/full-0.bin $base $((base + chunk))"
        echo "kill"
    } > "$T/optloop.gdb"
    bash -c "ulimit -s 1048576; gdb -q -batch -x '$T/optloop.gdb' --args '$J/optloop/out/$BIN' '$src' '$(unary "$CENSUS")'" > "$T/optloop-gdb.log" 2>&1
    census_tool rccensus2 || exit 1
    "$T/rccensus2" "$D/full-0.bin" | head -${CENSUS_LINES:-10}
    census_tool rcgraph2 || exit 1
    "$T/rcgraph2" "$D" 2> /dev/null | grep -E "^cells|^exclusive size" | head -${GRAPH_LINES:-10} | cut -c1-150
    exit 0
fi
for n in "$@"; do
    systemd-run --user --scope -q -p MemoryMax=8G -p MemorySwapMax=0 /usr/bin/time -f '%e %M' -o "$T/optloop.time" \
        bash -c "ulimit -s 1048576; '$J/optloop/out/$BIN' '$src' '$(unary "$n")'" > "$T/optloop.out" 2> /dev/null
    code=$?
    read -r secs kb < <(tail -1 "$T/optloop.time")
    printf 'rounds=%-5s exit=%-3s %7ss %9s MB  out=%s\n' "$n" "$code" "$secs" "$(awk -v k="${kb:-0}" 'BEGIN{printf "%.1f", k/1024}')" "$(head -1 "$T/optloop.out" | cut -c1-40)"
done
