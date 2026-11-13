#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../../.."
source_file=selfhost/packages/semantics/src/AshesCompiler/Semantics/CoreLowering.ash
tests=selfhost/tests/unused-lambda-references/Tests.ash
out=work/unused-lambda-references
mkdir -p "$out"
extract_declaration() {
    awk -v name="$1" '
        $0 ~ "^(let( recursive)?|type) " name "([ (]|$)" { found = 1; first = 1 }
        found && !first && /^(let( recursive)?|type) / { exit }
        found { print; first = 0 }
        END { if (!found) { print "Missing declaration: " name > "/dev/stderr"; exit 1 } }
    ' "$source_file"
}
{
    for declaration in containsName stripExprAt NonCalleeUses recordNonCalleeUse \
        patternBoundNames collectNonCalleeUses letBindingValuesNonCalleeUses \
        implementationReferenceUses defaultMethodReferenceUses providerReferenceUses \
        exportReferenceUses programNonCalleeUses unreferencedLambdaItem \
        retainReferencedLambdaItems validatedReplayItems; do
        extract_declaration "$declaration"
    done
} > "$out/ProductionHelpers.ash.inc"
awk -v helpers="$out/ProductionHelpers.ash.inc" '
    /^\/\/ PRODUCTION_HELPERS$/ {
        found++
        while ((getline line < helpers) > 0) print line
        close(helpers)
        next
    }
    { print }
    END { if (found != 1) { print "Expected one production-helper marker" > "/dev/stderr"; exit 1 } }
' "$tests" > "$out/Main.ash"
cat > "$out/ashes.json" <<'JSON'
{"name":"unused-lambda-references","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}
JSON
cp selfhost/tests/semantics/ashes.lock "$out/ashes.lock"
