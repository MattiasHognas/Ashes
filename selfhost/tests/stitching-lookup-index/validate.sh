#!/usr/bin/env bash
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../../.." && pwd)
cd -- "$root"
compiler=$(realpath -- "${1:?provide compiler path}")
tag=${2:?provide evidence tag}
seeds=${3:-4000}
case "$tag" in *[!a-zA-Z0-9_-]*) printf 'invalid evidence tag\n' >&2; exit 1 ;; esac
export UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
ulimit -s 1048576
evidence="work/stitching-lookup-index-$tag"
mkdir -p "$evidence"
scope() { local cap=$1; shift; systemd-run --user --scope -q -p MemoryMax="$cap" -p MemorySwapMax=0 "$@"; }
sha256sum "$compiler" > "$evidence/compiler.sha256"
git rev-parse HEAD > "$evidence/source-commit"
scope 8G /usr/bin/time -v -o "$evidence/compile.time" "$compiler" compile \
    --project selfhost/tests/stitching-lookup-index/ashes.json -o "$evidence/check" \
    > "$evidence/compile.stdout" 2> "$evidence/compile.stderr"

# The checker stops after a seed that ends in a missing module and names the seed to resume at.
: > "$evidence/check.stdout"
next=1
while [ "$next" -le "$seeds" ]; do
    line=$(scope 4G timeout 900 "$evidence/check" check "$next" "$seeds")
    printf '%s\n' "$line" >> "$evidence/check.stdout"
    case "$line" in 'ok next='*) ;; *) printf 'unexpected checker output: %s\n' "$line" >&2; exit 1 ;; esac
    resumed=${line#ok next=}
    resumed=${resumed%% *}
    [ "$resumed" -gt "$next" ] || { printf 'the checker made no progress at seed %s\n' "$next" >&2; exit 1; }
    next=$resumed
done
awk '{ for (field = 3; field <= NF; field++) { split($field, pair, "="); total[pair[1]] += pair[2]; if (!(pair[1] in seen)) { seen[pair[1]] = 1; order[++count] = pair[1] } } }
    END { for (index_ = 1; index_ <= count; index_++) printf "%s%s=%d", (index_ > 1 ? " " : ""), order[index_], total[order[index_]]; printf "\n" }' \
    "$evidence/check.stdout" | tee "$evidence/check.summary"

# Old and new must agree on the checksum of every benchmark shape; the timings are the evidence.
for shape in "24 150" "48 150" "24 600"; do
    name=${shape/ /x}
    for mode in old-build new-build old new; do
        scope 4G timeout 900 /usr/bin/time -f '%e s %M KiB' -o "$evidence/$mode-$name.time" \
            "$evidence/check" "$mode" $shape > "$evidence/$mode-$name.stdout"
    done
    cmp "$evidence/old-build-$name.stdout" "$evidence/new-build-$name.stdout"
    cmp "$evidence/old-$name.stdout" "$evidence/new-$name.stdout"
    printf '%s modules x definitions: build old %s, new %s; build and resolve old %s, new %s\n' "$name" \
        "$(cat "$evidence/old-build-$name.time")" "$(cat "$evidence/new-build-$name.time")" \
        "$(cat "$evidence/old-$name.time")" "$(cat "$evidence/new-$name.time")"
done
printf 'frozen stitching oracle passed\n'
