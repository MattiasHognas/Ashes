#!/usr/bin/env bash
# Load/latency benchmark for the Ashes TCP echo server.
#
# Client and server are both Ashes (dogfooding): echo.ash is the server, load.ash is a client that
# does `count` sequential connect/send/recv/close round-trips. This script compiles both, starts the
# server, and for each concurrency level runs that many load clients in parallel (each doing
# requests/concurrency round-trips), timing the batch with the shell clock to derive:
#   - throughput (req/s) across the whole batch
#   - mean round-trip latency = batch_elapsed / round-trips-per-worker (workers run in parallel)
#
# serve() handles connections sequentially today, so throughput falls and mean latency climbs as
# concurrency rises — that is the baseline the multi-reactor milestone will be A/B'd against. A loaded
# box adds variance; interleave runs when comparing builds.
#
# Usage: bench.sh [REQUESTS] [CONCURRENCY...]   e.g.  bench.sh 20000 1 8 64
set -euo pipefail

REQUESTS="${1:-20000}"
shift || true
CONC=("${@:-1 8 64}")
[ "${#CONC[@]}" -eq 1 ] && read -r -a CONC <<< "${CONC[0]}"

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
CLI="dotnet run --project $ROOT/src/Ashes.Cli -c Release --"
TMP="$(mktemp -d)"
trap 'kill "${SRV:-0}" 2>/dev/null || true; rm -rf "$TMP"' EXIT

echo "compiling echo.ash + load.ash ..."
$CLI compile "$ROOT/challenges/server/echo.ash" -o "$TMP/echo"  >/dev/null
$CLI compile "$ROOT/challenges/server/load.ash" -o "$TMP/load"  >/dev/null
chmod +x "$TMP/echo" "$TMP/load"

"$TMP/echo" >/dev/null 2>&1 &
SRV=$!

# Wait for the listener, and warm up.
for _ in $(seq 1 50); do
    if "$TMP/load" 1 2>/dev/null | grep -q ok; then break; fi
    sleep 0.1
done
"$TMP/load" 200 >/dev/null 2>&1 || true

printf "target 127.0.0.1:18080  requests/stage=%s\n\n" "$REQUESTS"
for c in "${CONC[@]}"; do
    per=$(( REQUESTS / c ))
    [ "$per" -lt 1 ] && per=1
    start=$(date +%s.%N)
    pids=()
    for _ in $(seq 1 "$c"); do
        "$TMP/load" "$per" >/dev/null 2>&1 &
        pids+=($!)
    done
    ok=1
    for p in "${pids[@]}"; do wait "$p" || ok=0; done
    end=$(date +%s.%N)
    total=$(( per * c ))
    awk -v s="$start" -v e="$end" -v total="$total" -v per="$per" -v c="$c" -v ok="$ok" 'BEGIN {
        el = e - s;
        label = (c == 1) ? "latency (concurrency 1)" : sprintf("load (concurrency %d)", c);
        printf "[%s]\n", label;
        printf "  requests: %d  workers: %d  elapsed: %.3fs%s\n", total, c, el, (ok ? "" : "  (a worker reported errors)");
        if (el > 0) {
            printf "  throughput: %.0f req/s\n", total / el;
            printf "  mean round-trip: %.3f ms\n", el * 1000 / per;
        }
        print "";
    }'
done
