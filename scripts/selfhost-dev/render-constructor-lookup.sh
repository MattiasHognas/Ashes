#!/usr/bin/env bash
# Build a focused test project from the actual Core constructor index declarations.
set -euo pipefail
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
fixture="$repo/selfhost/tests/constructor-lookup-index"
out="$repo/work/constructor-lookup-index"
mkdir -p "$out"
source "$repo/selfhost/tests/render-lib.sh"
core=$(core_lowering_source "$out/core-lowering-source.txt")
[[ $(rg -F -c 'type CoreConstructorLayout =' "$core") == 1 ]]
[[ $(rg -F -c 'type CoreBindingLocation =' "$core") == 1 ]]
[[ $(rg -F -c 'let constructorLookupIndex ' "$core") == 1 ]]
cat > "$out/Main.ash" <<'ASH'
import AshesCompiler.Semantics.Types
import Ashes.Collection.List.length
import Ashes.Collection.List.reverse
import Ashes.Collection.List.append
import Ashes.Collection.Map.MapTree
import AshesCompiler.Semantics.TaglessAdtLayout
ASH
extract_range "$core" '^type CoreConstructorLayout =' '^type CoreBindingLocation =' >> "$out/Main.ash"
extract_range "$core" '^let recursive splitConstructorType ' '^let instantiateConstructor ' >> "$out/Main.ash"
extract_range "$core" '^let recursive coreListLength ' '^let builtinArity ' >> "$out/Main.ash"
extract_range "$core" '^let recursive findRecordLayout ' '^let recordLayout ' >> "$out/Main.ash"
extract_range "$core" '^let recursive zeroCostPayloadType ' '^let stateOwnedTypeNameOf ' >> "$out/Main.ash"
extract_range "$core" '^let recursive firstLayoutOfType ' '^let shallowAdtCopySizeBytes ' >> "$out/Main.ash"
extract_range "$core" '^let recursive firstConstructorLayoutOf ' '^let ownedLayoutChildren ' >> "$out/Main.ash"
extract_range "$core" '^let recursive nullaryConstructorCount ' '^let provenanceConstructorsOf ' >> "$out/Main.ash"
cat "$fixture/Assertions.ash.inc" >> "$out/Main.ash"
cat > "$out/ashes.json" <<'JSON'
{"name":"ashes-constructor-lookup-index","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}
JSON
cp "$repo/selfhost/tests/heap-classification-plateau/ashes.lock" "$out/ashes.lock"
sha256sum "$core" "$out/Main.ash" > "$out/source-hashes.txt"
printf 'Generated %s\n' "$out/ashes.json"
