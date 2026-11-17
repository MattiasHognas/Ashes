#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../../.."
mode=${1:-state}
case "$mode" in
    state) fixture=Main ;;
    literal) fixture=LiteralControl ;;
    *) printf 'expected state or literal\n' >&2; exit 1 ;;
esac
out="work/pending-goal-lifetime-$mode"
mkdir -p "$out"
cp "selfhost/tests/pending-goal-lifetime/$fixture.ash" "$out/Main.ash"
cat > "$out/ashes.json" <<'JSON'
{"name":"logical-not-lifetime","entry":"Main.ash","sourceRoots":["."],"outDir":"out","devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}
JSON
cp selfhost/tests/semantics/ashes.lock "$out/ashes.lock"
printf '%s\n' "$out"
