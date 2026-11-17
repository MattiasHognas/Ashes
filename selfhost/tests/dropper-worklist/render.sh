#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../../.."
out=work/dropper-worklist
mkdir -p "$out"
awk '
    !found && /^export \(/ {
        print "export ("
        print "    value generalRcAdmissible,"
        print "    type DropperAdmissibilityVisited(..),"
        print "    type DropperAdmissibilityKey(..),"
        print "    value dropperAdmissibilityKey,"
        print "    value initialDropperAdmissibilityVisited,"
        print "    value dropperAdmissibilityVisitStatus,"
        print "    value dropperConstructorsOfType,"
        found=1
        next
    }
    { print }
    END { if (!found) { print "export block not found" > "/dev/stderr"; exit 1 } }
' selfhost/packages/semantics/src/AshesCompiler/Semantics/StructuralDroppers.ash > "$out/ProbeDroppers.ash"
for name in Main FrozenDecision FrozenLinearWorklist; do
    cp "selfhost/tests/dropper-worklist/$name.ash" "$out/$name.ash"
done
cat > "$out/ashes.json" <<'JSON'
{"name":"dropper-worklist","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}
JSON
cp selfhost/tests/dropper-view-check/ashes.lock "$out/ashes.lock"
