#!/usr/bin/env bash
set -euo pipefail
root=$(git rev-parse --show-toplevel)
compiler=${1:?Usage: bash validate.sh /absolute/path/to/ashes [plain|poison]}
mode=${2:-plain}
case "$mode" in plain|poison) ;; *) printf 'Unknown mode: %s\n' "$mode" >&2; exit 2 ;; esac
cd "$root"
evidence="work/inference-environment/$mode"
mkdir -p "$evidence"
export UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
export ASHES_RC_LEAKCHECK=1
if [[ $mode == poison ]]; then export ASHES_RC_POISON=1; else unset ASHES_RC_POISON; fi
bash /home/mattias/.claude/jobs/heavy2.sh systemd-run --user --scope -q -p MemoryMax=8G -p MemorySwapMax=0 \
    /usr/bin/time -v -o "$evidence/compile.time" "$compiler" compile --project selfhost/tests/inference-environment/ashes.json --debug -O0 -o "$evidence/check" >"$evidence/compile.stdout" 2>"$evidence/compile.stderr"
bash /home/mattias/.claude/jobs/heavy2.sh systemd-run --user --scope -q -p MemoryMax=4G -p MemorySwapMax=0 \
    /usr/bin/time -v -o "$evidence/run.time" "$evidence/check" >"$evidence/run.stdout" 2>"$evidence/run.stderr"
printf 'captured environment inference: 25 cases passed\n' >"$evidence/expected.stdout"
cmp "$evidence/expected.stdout" "$evidence/run.stdout"
[[ $(rg -c '^ashes-rc-live 1$' "$evidence/run.stderr") == 1 ]]
[[ $(rg -c '^ashes-rc-live ' "$evidence/run.stderr") == 1 ]]
printf '%s inference regression passed; final live cells 1\n' "$mode"
