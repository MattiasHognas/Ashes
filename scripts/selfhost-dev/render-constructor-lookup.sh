#!/usr/bin/env bash
# Build a focused test project from the actual Core constructor index declarations.
set -euo pipefail
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
core="$repo/selfhost/packages/semantics/src/AshesCompiler/Semantics/CoreLowering.ash"
fixture="$repo/selfhost/tests/constructor-lookup-index"
out="$repo/work/constructor-lookup-index"
[[ $(rg -F -c 'type CoreConstructorLayout =' "$core") == 1 ]]
[[ $(rg -F -c 'type CoreBindingLocation =' "$core") == 1 ]]
[[ $(rg -F -c 'let constructorLookupIndex ' "$core") == 1 ]]
mkdir -p "$out"
cat > "$out/Main.ash" <<'ASH'
import AshesCompiler.Semantics.Types
import Ashes.Collection.List.length
import Ashes.Collection.List.reverse
import Ashes.Collection.Map.MapTree
ASH
awk '/^type CoreConstructorLayout =/ {copying=1} copying && /^type CoreBindingLocation =/ {exit} copying {print}' "$core" >> "$out/Main.ash"
cat "$fixture/Assertions.ash.inc" >> "$out/Main.ash"
cat > "$out/ashes.json" <<'JSON'
{"name":"ashes-constructor-lookup-index","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}
JSON
cp "$repo/selfhost/tests/heap-classification-plateau/ashes.lock" "$out/ashes.lock"
sha256sum "$core" "$out/Main.ash" > "$out/source-hashes.txt"
printf 'Generated %s\n' "$out/ashes.json"
