#!/usr/bin/env bash
set -euo pipefail
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
out="$repo/work/returned-closure-index"
mkdir -p "$out"
cat > "$out/Main.ash" <<'ASH'
import Ashes.Collection.Map.MapTree
import AshesCompiler.Semantics.CallOwnership.innermostStageLabel
ASH
awk '/^let returnedStageOf / {copying=1} copying && /^\/\/ The names the function/ {exit} copying {print}' "$repo/selfhost/packages/semantics/src/AshesCompiler/Semantics/CoreLowering.ash" >> "$out/Main.ash"
cat "$repo/selfhost/tests/returned-closure-index/Assertions.ash.inc" >> "$out/Main.ash"
cat > "$out/ashes.json" <<'JSON'
{"name":"returned-closure-index","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}
JSON
cp "$repo/selfhost/tests/call-reach-index/ashes.lock" "$out/ashes.lock"
