#!/usr/bin/env bash
# Compares the .NET compiler's front phases with the self-hosted compiler's phases over the same
# corpus. Builds the stage-1 bench program with the .NET compiler at -O2, builds the .NET bench
# driver, then runs the driver, which times its own phases in-process and the stage-1 binary per
# phase, and prints one table.
#
# Usage:
#   selfhost/bench/run.sh [--iterations N] [--corpus <dir>]...
#
# Env: ASHES_BENCH_OPT (default -O2) selects the optimization level of the stage-1 bench binary.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OPT="${ASHES_BENCH_OPT:--O2}"
BIN="$(mktemp)"
trap 'rm -f "$BIN"' EXIT

echo "compiling the stage-1 bench ($OPT) ..." >&2
dotnet run --project "$ROOT/src/Ashes.Cli" -c Release -- compile --project "$ROOT/selfhost/bench/stage1/ashes.json" -o "$BIN" "$OPT" >/dev/null
chmod +x "$BIN"

echo "building the stage-0 bench driver ..." >&2
dotnet build "$ROOT/selfhost/bench/Ashes.SelfhostBench/Ashes.SelfhostBench.csproj" -c Release >/dev/null

cd "$ROOT"
dotnet run --no-build --project "$ROOT/selfhost/bench/Ashes.SelfhostBench/Ashes.SelfhostBench.csproj" -c Release -- --stage1 "$BIN" "$@"
