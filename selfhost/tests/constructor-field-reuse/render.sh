#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../../.."
core=selfhost/packages/semantics/src/AshesCompiler/Semantics/CoreLowering.ash
out=work/constructor-field-reuse
mkdir -p "$out"
extract() {
    awk -v name="$1" '
        $0 ~ "^(let( recursive)?|type) " name "([ (]|$)" { found=1; first=1 }
        found && !first && /^(let( recursive)?|type) / { exit }
        found { print; first=0 }
        END { if (!found) exit 1 }
    ' "$core"
}
awk '/^type CoreConstructorLayout =/ {copying=1} copying && /^type CoreBindingLocation =/ {exit} copying {print}' "$core" > "$out/index.ash.inc"
{
    for name in resolveType splitConstructorType constructorResultName layoutFieldTypes lookupTypeVariablePair replaceTypeVariables constructorResultNameIs typeVariablePairs namedTypeConstructorFieldTypes namedTypeInstantiatedFieldTypes stateConstructorsOfType stateStableConstructorFields namedTypeFieldTypesAs namedTypeDeclaredFields; do
        extract "$name"
    done
} > "$out/helpers.ash.inc"
awk -v helpers="$out/helpers.ash.inc" -v index_file="$out/index.ash.inc" '
 /^\/\/ PRODUCTION_INDEX$/ { indexed++; while ((getline line < index_file)>0) print line; close(index_file); next }
 /^\/\/ PRODUCTION_HELPERS$/ { found++; while ((getline line < helpers)>0) print line; close(helpers); next }
 {print}
 END {if(found!=1 || indexed!=1)exit 1}
' selfhost/tests/constructor-field-reuse/Tests.ash > "$out/Main.ash"
cat > "$out/ashes.json" <<'JSON'
{"name":"constructor-field-reuse","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}
JSON
cp selfhost/tests/semantics/ashes.lock "$out/ashes.lock"
sha256sum "$core" selfhost/tests/constructor-field-reuse/Tests.ash "$out/Main.ash" > "$out/source-hashes.txt"
