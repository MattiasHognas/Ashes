#!/usr/bin/env bash
set -euo pipefail
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
out="$repo/work/heap-drop-worklist"
source="$repo/selfhost/packages/semantics/src/AshesCompiler/Semantics/HeapLayoutClassification.ash"
mkdir -p "$out"
awk '/^export \(/ {skip=1;next} skip && /^\)/ {skip=0;next} !skip {print}' "$source" > "$out/Main.ash"
cat "$repo/selfhost/tests/heap-drop-worklist/Reference.ash.inc" "$repo/selfhost/tests/heap-drop-worklist/Assertions.ash.inc" >> "$out/Main.ash"
cat > "$out/ashes.json" <<'JSON'
{"name":"ashes-heap-drop-worklist","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}
JSON
cp "$repo/selfhost/tests/heap-drop-worklist/ashes.lock" "$out/ashes.lock"
sha256sum "$source" "$out/Main.ash" > "$out/source-hashes.txt"
