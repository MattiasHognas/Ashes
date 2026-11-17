#!/usr/bin/env bash
set -euo pipefail
fixture=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
root=$(cd -- "$fixture/../../.." && pwd)
source "$root/selfhost/tests/render-lib.sh"
out="$root/work/reach-constructor-index"
mkdir -p "$out"
path=selfhost/packages/semantics/src/AshesCompiler/Semantics/ResultReachSummaries.ash
old="$out/ReferenceResultReachSummaries.ash.inc"
git -C "$root" show "61ff1e6a:$path" > "$old"
prefix_module_exports "$old" Ref ref_ > "$out/ReferenceResultReachSummaries.ash"
cp "$fixture/Main.ash" "$out/Main.ash"
printf '%s' '{"name":"reach-constructor-index","entry":"Main.ash","sourceRoots":["."],"devDependencies":{"semantics":{"path":"../../selfhost/packages/semantics"}},"overrides":{"AshesCompiler.Frontend":{"path":"../../selfhost/packages/frontend"}}}' > "$out/ashes.json"
cp "$root/selfhost/tests/type-graph-path-cut/ashes.lock" "$out/ashes.lock"
printf '%s\n' "$out/ashes.json"
