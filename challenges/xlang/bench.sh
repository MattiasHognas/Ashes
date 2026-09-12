#!/usr/bin/env bash
# Cross-language comparison harness for the compute-bound Benchmarks Game challenges.
#
# Builds the Ashes program and the .NET/Rust/Go/OCaml ports of the same benchmark, checks every
# port's output against the Ashes output, and reports hyperfine wall-clock means.
#
# Output comparison normalizes trailing blank lines: the Ashes programs print a string that already
# ends in a newline, so io.print adds one more. Everything before that must match byte for byte.
#
# Usage:
#   challenges/xlang/bench.sh                 # every benchmark at its standard workload
#   challenges/xlang/bench.sh nbody 1000      # one benchmark at a chosen workload
#
# Env: RUNS (default 3), WARMUP (default 1), LANGS (default "ashes dotnet rust go ocaml").
# A toolchain that is not installed is reported as skipped rather than failing the run.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HERE="$ROOT/challenges/xlang"
RUNS="${RUNS:-3}"
WARMUP="${WARMUP:-1}"
LANGS="${LANGS:-ashes dotnet rust go ocaml}"
BUILD="$(mktemp -d)"
trap 'rm -rf "$BUILD"' EXIT

# benchmark -> challenges/<dir>/<dir>.ash, standard workload
ashes_dir () { case "$1" in
  nbody) echo n-body ;; spectralnorm) echo spectral-norm ;;
  binarytrees) echo binary-trees ;; fannkuch) echo fannkuch-redux ;;
esac; }
standard_arg () { case "$1" in
  nbody) echo 50000000 ;; spectralnorm) echo 5500 ;;
  binarytrees) echo 21 ;; fannkuch) echo 11 ;;
esac; }

have () { command -v "$1" >/dev/null 2>&1; }

build_one () { # $1=benchmark; echoes nothing, sets BIN_<lang>
  local b="$1" dir; dir="$(ashes_dir "$b")"
  BIN_ashes=""; BIN_dotnet=""; BIN_rust=""; BIN_go=""; BIN_ocaml=""
  for lang in $LANGS; do case "$lang" in
    ashes)
      dotnet run --project "$ROOT/src/Ashes.Cli" -c Release -- \
        compile "$ROOT/challenges/$dir/$dir.ash" -O2 -o "$BUILD/$b.ashes" >/dev/null 2>&1 \
        && BIN_ashes="$BUILD/$b.ashes" ;;
    dotnet)
      have dotnet && dotnet publish "$HERE/bench.csproj" -c Release -p:Bench="$b" \
        -o "$BUILD/cs-$b" >/dev/null 2>&1 && BIN_dotnet="$BUILD/cs-$b/bench" ;;
    rust)
      have rustc && rustc -O -o "$BUILD/$b.rs.bin" "$HERE/rs/$b.rs" >/dev/null 2>&1 \
        && BIN_rust="$BUILD/$b.rs.bin" ;;
    go)
      have go && (cd "$BUILD" && go mod init xlang >/dev/null 2>&1;
        go build -o "$BUILD/$b.go.bin" "$HERE/go/$b.go" >/dev/null 2>&1) \
        && BIN_go="$BUILD/$b.go.bin" ;;
    ocaml)
      # The distro ocamlopt is often built without flambda, so -O3 is unavailable; -inline is not.
      # Compiled from a copy: ocamlopt writes .cmi/.cmx/.o beside the SOURCE, which would leave
      # build outputs in the repository.
      have ocamlopt && cp "$HERE/ml/$b.ml" "$BUILD/" && (cd "$BUILD" && ocamlopt -unsafe -inline 100 \
        -o "$BUILD/$b.ml.bin" "$b.ml" >/dev/null 2>&1) \
        && BIN_ocaml="$BUILD/$b.ml.bin" ;;
  esac; done
}

# Strip trailing blank lines so the Ashes extra newline does not count as a difference.
canonical () { "$@" 2>/dev/null | sed -e :a -e '/^\n*$/{$d;N;};/\n$/ba' | md5sum | cut -c1-12; }

bench_one () { # $1=benchmark $2=arg
  local b="$1" arg="$2" ref="" lang bin mean got status
  printf '\n=== %s %s ===\n' "$b" "$arg"
  build_one "$b"
  [ -n "$BIN_ashes" ] && ref="$(canonical "$BIN_ashes" "$arg")"
  for lang in $LANGS; do
    eval "bin=\${BIN_$lang}"
    if [ -z "$bin" ]; then printf '  %-7s %s\n' "$lang" "skipped (toolchain or build unavailable)"; continue; fi
    got="$(canonical "$bin" "$arg")"
    if [ -z "$ref" ]; then status="no reference"; elif [ "$got" = "$ref" ]; then status="output OK"; else status="OUTPUT DIFFERS"; fi
    mean="$(hyperfine -N --warmup "$WARMUP" --runs "$RUNS" --style none \
      --export-json /dev/stdout "$bin $arg" 2>/dev/null \
      | grep -o '"mean": *[0-9.]*' | head -1 | grep -o '[0-9.]*')"
    printf '  %-7s %8.3fs  %s\n' "$lang" "${mean:-0}" "$status"
  done
}

have hyperfine || { echo "hyperfine is required" >&2; exit 1; }
if [ "$#" -ge 1 ]; then
  bench_one "$1" "${2:-$(standard_arg "$1")}"
else
  for b in spectralnorm binarytrees nbody fannkuch; do bench_one "$b" "$(standard_arg "$b")"; done
fi
