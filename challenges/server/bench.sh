#!/usr/bin/env bash
# Load/latency benchmark comparing the Ashes TCP echo server against a .NET echo server.
#
# The SAME fast .NET load generator (loadgen.cs) drives BOTH servers, so the comparison isolates the
# server (same functionality — an echo server — same client), rather than mixing an Ashes client with
# a .NET server. Each request is one connection: connect -> send -> read echo -> close. The load
# generator does the concurrency and timing itself and reports throughput + latency percentiles.
#
# Servers compared (both on 127.0.0.1:18080, one at a time):
#   - ashes:  challenges/server/echo.ash        (Ashes.Net.Tcp.Server.serve, sequential today)
#   - dotnet: challenges/server/dotnet-echo.cs  (concurrent async accept loop, the natural .NET idiom)
#
# serve() is sequential while the .NET baseline is concurrent, so the gap at higher concurrency is the
# headroom the multi-reactor milestone targets. A loaded box adds variance; interleave A/B runs.
#
# Everything is built once to plain executables (the Ashes server via the compiler, the .NET pieces via
# `dotnet publish`), then run directly — no per-run `dotnet run`.
#
# Usage: bench.sh [REQUESTS] [CONCURRENCY...]   e.g.  bench.sh 20000 1 8 64
set -euo pipefail

REQUESTS="${1:-20000}"
shift || true
CONC=("${@:-1 8 64}")
[ "${#CONC[@]}" -eq 1 ] && read -r -a CONC <<< "${CONC[0]}"

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HERE="$ROOT/challenges/server"
PORT=18080
TMP="$(mktemp -d)"
SRV=""
trap 'kill "$SRV" 2>/dev/null || true; rm -rf "$TMP"' EXIT

echo "building echo.ash, dotnet-echo.cs, loadgen.cs ..."
dotnet run --project "$ROOT/src/Ashes.Cli" -c Release -- compile "$HERE/echo.ash" -o "$TMP/echo" >/dev/null
chmod +x "$TMP/echo"
dotnet publish "$HERE/dotnet-echo.cs" -o "$TMP/dotnet-echo" >/dev/null
dotnet publish "$HERE/loadgen.cs"     -o "$TMP/loadgen"     >/dev/null
LOAD="$TMP/loadgen/loadgen"

run_against() {  # $1 = label; $2.. = server command (backgrounded); assumes it listens on $PORT
    local label="$1"; shift
    "$@" >/dev/null 2>&1 & SRV=$!
    echo "== $label =="
    for c in "${CONC[@]}"; do
        printf "  %-7s" "$label"
        "$LOAD" "$REQUESTS" "$c" "$PORT"
    done
    kill "$SRV" 2>/dev/null || true; SRV=""
    sleep 0.3
    echo
}

printf "\ntarget 127.0.0.1:%s  requests/stage=%s  concurrency=[%s]\n\n" "$PORT" "$REQUESTS" "${CONC[*]}"
run_against "ashes"  "$TMP/echo"
run_against "dotnet" "$TMP/dotnet-echo/dotnet-echo" "$PORT"
