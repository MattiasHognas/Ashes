#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../../.."
base=work/type-graph-edges
mkdir -p "$base/check" "$base/benchmark"
cp selfhost/tests/type-graph-edges/Main.ash "$base/check/Main.ash"
cp selfhost/tests/type-graph-edges/Benchmark.ash "$base/benchmark/Main.ash"
for project in check benchmark; do
    cp selfhost/tests/semantics/ashes.lock "$base/$project/ashes.lock"
    cat > "$base/$project/ashes.json" <<'JSON'
{"name":"type-graph-edges","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../../selfhost/packages/frontend"}}}
JSON
done
sha256sum selfhost/packages/semantics/src/AshesCompiler/Semantics/{TypeGraphIndex,TypeGraphQueries}.ash selfhost/tests/type-graph-edges/{Main,Benchmark}.ash > "$base/source-hashes.txt"
