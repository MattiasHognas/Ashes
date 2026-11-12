#!/usr/bin/env bash
set -euo pipefail
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
core="$repo/selfhost/packages/semantics/src/AshesCompiler/Semantics/CoreLowering.ash"
fixture="$repo/selfhost/tests/join-backedge-provenance"
out="$repo/work/join-backedge-provenance"
mkdir -p "$out"
# Extract the exact production predicates; the fixture supplies only the narrow state boundary.
rg -Fq 'armStoreTemp = if armNeverReachesJoin(finalTemp)(state)' "$core"
rg -Fq '|> recordNeutralJoin(resultTemp)(arms)' "$core"
cat "$fixture/Prelude.ash.inc" > "$out/Main.ash"
awk '/^let armNeverReachesJoin / {copying=1} copying && /^let recursive patternFactsNamed / {exit} copying {print}' "$core" >> "$out/Main.ash"
awk '/^let matchArmResultWith / {copying=1} copying && /^let armPatternRootIsPlaced / {exit} copying {print}' "$core" >> "$out/Main.ash"
awk '/^let recursive anyArmReachesJoin / {copying=1} copying && /^let reachingArmsOwnTheirReference / {exit} copying {print}' "$core" >> "$out/Main.ash"
awk '/^let recordNeutralJoin / {copying=1} copying && /^let markControlFlowJoin / {exit} copying {print}' "$core" >> "$out/Main.ash"
cat "$fixture/Assertions.ash.inc" >> "$out/Main.ash"
cat > "$out/ashes.json" <<'JSON'
{"name":"join-backedge-provenance","entry":"Main.ash","sourceRoots":["."],"devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}
JSON
cp "$repo/selfhost/tests/heap-classification-plateau/ashes.lock" "$out/ashes.lock"
sha256sum "$core" "$out/Main.ash" > "$out/source-hashes.txt"
