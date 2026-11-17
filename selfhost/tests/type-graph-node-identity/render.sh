#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../../.."
out=work/type-graph-node-identity
mkdir -p "$out"
cp selfhost/tests/type-graph-node-identity/Main.ash "$out/Main.ash"
cp selfhost/tests/semantics/TypeGraphIndexTests.ash "$out/TypeGraphIndexTests.ash"
cp selfhost/tests/semantics/ashes.lock "$out/ashes.lock"
cat > "$out/ashes.json" <<'JSON'
{"name":"type-graph-node-identity","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}
JSON
sha256sum selfhost/packages/semantics/src/AshesCompiler/Semantics/TypeGraphIndex.ash selfhost/packages/semantics/src/AshesCompiler/Semantics/TypeGraphQueries.ash selfhost/packages/semantics/src/AshesCompiler/Semantics/HeapLayoutClassification.ash "$out/Main.ash" "$out/TypeGraphIndexTests.ash" > "$out/source-hashes.txt"
