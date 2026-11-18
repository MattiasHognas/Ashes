#!/usr/bin/env bash
set -euo pipefail
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
fixture="$repo/selfhost/tests/join-backedge-provenance"
out="$repo/work/join-backedge-provenance"
mkdir -p "$out"
source "$repo/selfhost/tests/render-lib.sh"
core=$(core_lowering_source "$out/core-lowering-source.txt")
# Extract the exact production predicates; the fixture supplies only the narrow state boundary.
rg -Fq 'armStoreTemp = if armNeverReachesJoin(finalTemp)(state)' "$core"
rg -Fq '|> recordNeutralJoin(resultTemp)(arms)' "$core"
cat "$fixture/Prelude.ash.inc" > "$out/Main.ash"
extract_range "$core" '^let armNeverReachesJoin ' '^let recursive patternFactsNamed ' >> "$out/Main.ash"
extract_range "$core" '^let matchArmResultWith ' '^let armPatternRootIsPlaced ' >> "$out/Main.ash"
extract_range "$core" '^let recursive anyArmReachesJoin ' '^let reachingArmsOwnTheirReference ' >> "$out/Main.ash"
extract_range "$core" '^let recordNeutralJoin ' '^let markControlFlowJoin ' >> "$out/Main.ash"
cat "$fixture/Assertions.ash.inc" >> "$out/Main.ash"
cat > "$out/ashes.json" <<'JSON'
{"name":"join-backedge-provenance","entry":"Main.ash","sourceRoots":["."],"devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}
JSON
cp "$repo/selfhost/tests/heap-classification-plateau/ashes.lock" "$out/ashes.lock"
sha256sum "$core" "$out/Main.ash" > "$out/source-hashes.txt"
