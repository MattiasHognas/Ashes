#!/usr/bin/env bash
# Compile-and-time harness for the compute-bound Benchmarks Game challenges in this folder
# (the server benchmark has its own bespoke bench.sh; 1brc documents its own hyperfine command).
#
# Compiles challenges/<name>/<name>.ash at -O2 to a throwaway binary, then reports wall-clock time
# (hyperfine) and peak RSS (GNU time). Program arguments are passed through; a stdin fixture can be
# supplied with BENCH_STDIN=<file> (used by the FASTA-consuming challenges).
#
# Usage:
#   challenges/bench.sh <name> [program args...]
#   challenges/bench.sh n-body 50000000
#   BENCH_STDIN=/tmp/fasta25m.txt challenges/bench.sh reverse-complement
#
# Env: RUNS (default 5), WARMUP (default 1).
set -euo pipefail

NAME="${1:?usage: challenges/bench.sh <name> [program args...]}"
shift || true

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="$ROOT/challenges/$NAME/$NAME.ash"
[ -f "$SRC" ] || { echo "no challenge program at $SRC" >&2; exit 1; }

RUNS="${RUNS:-5}"
WARMUP="${WARMUP:-1}"
STDIN_FILE="${BENCH_STDIN:-/dev/null}"

BIN="$(mktemp)"
trap 'rm -f "$BIN"' EXIT

echo "compiling $NAME.ash -O2 ..."
dotnet run --project "$ROOT/src/Ashes.Cli" -c Release -- compile "$SRC" -o "$BIN" -O2 >/dev/null
chmod +x "$BIN"

printf '\n=== %s %s (stdin: %s) ===\n' "$NAME" "$*" "$STDIN_FILE"
hyperfine -N --warmup "$WARMUP" --runs "$RUNS" --input "$STDIN_FILE" "$BIN $*"

# Peak resident set size, reported once (GNU time; kbytes -> MB).
rss_kb="$(/usr/bin/time -v "$BIN" "$@" <"$STDIN_FILE" 2>&1 >/dev/null | awk '/Maximum resident/ {print $NF}')"
awk -v k="$rss_kb" 'BEGIN { printf "  peak RSS: %.1f MB\n", k/1024 }'
