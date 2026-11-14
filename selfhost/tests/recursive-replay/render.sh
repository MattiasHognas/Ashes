#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../../.."
source selfhost/tests/render-lib.sh
out=work/recursive-replay
fixture=selfhost/tests/recursive-replay
mkdir -p "$out"
{
    cat "$fixture/Prelude.ash"
    extract_declaration selfhost/packages/semantics/src/AshesCompiler/Semantics/CoreLowering.ash lowerRecursiveMembersPinningSiblingResults
    cat "$fixture/Assertions.ash"
} > "$out/Main.ash"
cat > "$out/ashes.json" <<'JSON'
{"name":"recursive-replay","entry":"Main.ash","sourceRoots":["."],"outDir":"out"}
JSON
cp "$fixture/expected.txt" "$out/expected.txt"
