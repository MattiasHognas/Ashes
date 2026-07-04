#!/usr/bin/env bash
# Load/latency benchmark for the Ashes TCP echo server, with an optional .NET baseline to compare to.
#
# Client and server are both Ashes (dogfooding): echo.ash is the server, load.ash is a client that
# does `count` sequential connect/send/recv/close round-trips. This script compiles both, starts the
# server, and for each concurrency level runs that many load clients in parallel (each doing
# requests/concurrency round-trips), timing the batch with the shell clock to derive:
#   - throughput (req/s) across the whole batch
#   - mean round-trip latency = batch_elapsed / round-trips-per-worker (workers run in parallel)
#
# If `dotnet` is available it then runs the SAME load against a single-file .NET echo server
# (dotnet-echo.cs) so the Ashes numbers have a reference point. Both listen on port 18080 and are run
# one at a time. Note: serve() is sequential today while the .NET baseline is concurrent, so the gap
# is the headroom the multi-reactor milestone targets. A loaded box adds variance; interleave A/B runs.
#
# Usage: bench.sh [REQUESTS] [CONCURRENCY...]   e.g.  bench.sh 20000 1 8 64
set -euo pipefail

REQUESTS="${1:-20000}"
shift || true
CONC=("${@:-1 8 64}")
[ "${#CONC[@]}" -eq 1 ] && read -r -a CONC <<< "${CONC[0]}"

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HERE="$ROOT/challenges/server"
CLI="dotnet run --project $ROOT/src/Ashes.Cli -c Release --"
TMP="$(mktemp -d)"
SRV=""
trap 'kill "$SRV" 2>/dev/null || true; rm -rf "$TMP"' EXIT

echo "compiling echo.ash + load.ash ..."
$CLI compile "$HERE/echo.ash" -o "$TMP/echo" >/dev/null
$CLI compile "$HERE/load.ash" -o "$TMP/load" >/dev/null
chmod +x "$TMP/echo" "$TMP/load"

wait_ready() {  # retry the load client until the listener answers
    for _ in $(seq 1 100); do
        if "$TMP/load" 1 2>/dev/null | grep -q ok; then return 0; fi
        sleep 0.1
    done
    return 1
}

run_sweep() {  # $1 = label; assumes a server is listening on 18080
    local label="$1"
    "$TMP/load" 200 >/dev/null 2>&1 || true   # warm up
    for c in "${CONC[@]}"; do
        local per=$(( REQUESTS / c )); [ "$per" -lt 1 ] && per=1
        local start end pids=()
        start=$(date +%s.%N)
        for _ in $(seq 1 "$c"); do "$TMP/load" "$per" >/dev/null 2>&1 & pids+=($!); done
        for p in "${pids[@]}"; do wait "$p" || true; done
        end=$(date +%s.%N)
        awk -v s="$start" -v e="$end" -v total="$(( per * c ))" -v per="$per" -v c="$c" -v lbl="$label" 'BEGIN {
            el = e - s;
            stage = (c == 1) ? "concurrency 1" : sprintf("concurrency %d", c);
            printf "  %-8s %-14s throughput %8.0f req/s   mean rtt %6.3f ms\n", lbl, stage, (el>0?total/el:0), (el>0?el*1000/per:0);
        }'
    done
}

printf "\ntarget 127.0.0.1:18080  requests/stage=%s  concurrency=[%s]\n\n" "$REQUESTS" "${CONC[*]}"

echo "== ashes (serve, sequential) =="
"$TMP/echo" >/dev/null 2>&1 & SRV=$!
wait_ready || { echo "ashes server did not come up"; exit 1; }
run_sweep "ashes"
kill "$SRV" 2>/dev/null || true; SRV=""
sleep 0.3

if command -v dotnet >/dev/null 2>&1; then
    echo
    echo "== dotnet (concurrent baseline) =="
    dotnet run "$HERE/dotnet-echo.cs" 18080 >/dev/null 2>&1 & SRV=$!
    wait_ready || { echo "dotnet server did not come up"; exit 1; }
    run_sweep "dotnet"
    kill "$SRV" 2>/dev/null || true; SRV=""
fi
echo
