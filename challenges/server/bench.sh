#!/usr/bin/env bash
# Load/latency benchmarks comparing the Ashes servers against equivalent .NET baselines. Two
# benchmarks, each isolating one server path:
#
#   TCP  — challenges/server/tcp_echo.ash   vs  dotnet-tcp.cs   on 127.0.0.1:18080
#          one connection per request: connect -> send "ping" -> read echo -> close
#   HTTP — challenges/server/http_echo.ash  vs  dotnet-http.cs  on 127.0.0.1:18081
#          one connection per request: connect -> send a GET -> read the 200 "ok" response -> close
#
# The SAME fast .NET load generator (loadgen.cs) drives both servers in each benchmark, so the
# comparison isolates the server (same functionality, same client) rather than mixing an Ashes client
# with a .NET server. The .NET baselines use a concurrent async accept loop (the natural .NET idiom, so
# they show the ceiling); the current Ashes serve() steps handlers cooperatively on one thread, so the
# gap at higher concurrency is the headroom the multi-reactor milestone targets. A loaded box adds
# variance; interleave A/B runs.
#
# Everything is built once to plain executables (the Ashes servers via the compiler, the .NET pieces
# via `dotnet publish`), then run directly — no per-run `dotnet run`.
#
# Usage: bench.sh [REQUESTS] [CONCURRENCY...]   e.g.  bench.sh 20000 1 8 64
set -euo pipefail

REQUESTS="${1:-20000}"
shift || true
CONC=("${@:-1 8 64}")
[ "${#CONC[@]}" -eq 1 ] && read -r -a CONC <<< "${CONC[0]}"

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HERE="$ROOT/challenges/server"
TCP_PORT=18080
HTTP_PORT=18081
TMP="$(mktemp -d)"
SRV=""
trap 'kill "$SRV" 2>/dev/null || true; rm -rf "$TMP"' EXIT

echo "building tcp_echo.ash, http_echo.ash, dotnet-tcp.cs, dotnet-http.cs, loadgen.cs ..."
dotnet run --project "$ROOT/src/Ashes.Cli" -c Release -- compile "$HERE/tcp_echo.ash"  -o "$TMP/tcp_echo"  >/dev/null
dotnet run --project "$ROOT/src/Ashes.Cli" -c Release -- compile "$HERE/http_echo.ash" -o "$TMP/http_echo" >/dev/null
chmod +x "$TMP/tcp_echo" "$TMP/http_echo"
dotnet publish "$HERE/dotnet-tcp.cs"  -o "$TMP/dotnet-tcp"  >/dev/null
dotnet publish "$HERE/dotnet-http.cs" -o "$TMP/dotnet-http" >/dev/null
dotnet publish "$HERE/loadgen.cs"     -o "$TMP/loadgen"     >/dev/null
LOAD="$TMP/loadgen/loadgen"

run_against() {  # $1 = label; $2 = port; $3 = mode; $4.. = server command (backgrounded)
    local label="$1" port="$2" mode="$3"; shift 3
    "$@" >/dev/null 2>&1 & SRV=$!
    for c in "${CONC[@]}"; do
        printf "  %-7s" "$label"
        "$LOAD" "$REQUESTS" "$c" "$port" "$mode"
    done
    kill "$SRV" 2>/dev/null || true; SRV=""
    sleep 0.3
}

printf "\n=== TCP echo   127.0.0.1:%s  requests/stage=%s  concurrency=[%s] ===\n" "$TCP_PORT" "$REQUESTS" "${CONC[*]}"
run_against "ashes"  "$TCP_PORT" tcp "$TMP/tcp_echo"
run_against "dotnet" "$TCP_PORT" tcp "$TMP/dotnet-tcp/dotnet-tcp" "$TCP_PORT"

printf "\n=== HTTP 200   127.0.0.1:%s  requests/stage=%s  concurrency=[%s] ===\n" "$HTTP_PORT" "$REQUESTS" "${CONC[*]}"
run_against "ashes"  "$HTTP_PORT" http "$TMP/http_echo"
run_against "dotnet" "$HTTP_PORT" http "$TMP/dotnet-http/dotnet-http" "$HTTP_PORT"
echo
