#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../../.."
source selfhost/tests/render-lib.sh
out=work/mixed-constrained-bootstrap
mkdir -p "$out"
extract_declaration selfhost/packages/semantics/src/AshesCompiler/Semantics/IrOptimizer.ash lookupAssociation > "$out/Lookup.ash"
for fixture in Main AliasMixedLookup PartialMixedLookup EscapedMixedLookup; do
awk -v helper="$out/Lookup.ash" '
    /^\/\/ OPTIMIZER_LOOKUP$/ {
        found++
        while ((getline line < helper) > 0) print line
        close(helper)
        next
    }
    { print }
    END { if (found != 1) exit 1 }
' "selfhost/tests/mixed-constrained-bootstrap/$fixture.ash" > "$out/$fixture.ash"
done
cp selfhost/tests/mixed-constrained-bootstrap/InlineEqualityControl.ash "$out/InlineEqualityControl.ash"
awk '
    /^\/\/ expect:/ { next }
    /^let recursive loop / {
        print "let roundCount ="
        print "    match Ashes.IO.args with"
        print "        | \"1\" :: [] -> 1"
        print "        | \"1000\" :: [] -> 1000"
        print "        | \"10000\" :: [] -> 10000"
        print "        | _ -> Ashes.IO.panic(\"count\")"
        print ""
        injected++
    }
    /\|> loop\(3\)/ { sub(/loop\(3\)/, "loop(roundCount)"); changed++ }
    { print }
    END { if (injected != 1 || changed != 1) exit 1 }
' tests/constrained_loop_lowered_at_caller_types_releases_snapshots.ash > "$out/SnapshotLoop.ash"
sha256sum selfhost/packages/semantics/src/AshesCompiler/Semantics/IrOptimizer.ash \
    tests/constrained_loop_lowered_at_caller_types_releases_snapshots.ash \
    selfhost/tests/mixed-constrained-bootstrap/{Main.ash,AliasMixedLookup.ash,PartialMixedLookup.ash,EscapedMixedLookup.ash,InlineEqualityControl.ash,render.sh} > "$out/inputs.sha256"
