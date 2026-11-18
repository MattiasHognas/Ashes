#!/usr/bin/env bash
set -euo pipefail
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
out="$repo/work/returned-closure-index"
mkdir -p "$out"
source "$repo/selfhost/tests/render-lib.sh"
core=$(core_lowering_source "$out/core-lowering-source.txt")
cat > "$out/Main.ash" <<'ASH'
import Ashes.Collection.Map.MapTree
import AshesCompiler.Semantics.CallOwnership.innermostStageLabel
ASH
extract_range "$core" '^let returnedStageOf ' '^// The names the function' >> "$out/Main.ash"
cat "$repo/selfhost/tests/returned-closure-index/Assertions.ash.inc" >> "$out/Main.ash"
cat > "$out/ashes.json" <<'JSON'
{"name":"returned-closure-index","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}
JSON
cp "$repo/selfhost/tests/call-reach-index/ashes.lock" "$out/ashes.lock"
