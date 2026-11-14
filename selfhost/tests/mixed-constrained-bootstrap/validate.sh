#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../../.."
compiler=$(realpath -- "${1:?compiler executable}")
tag=${2:?unique evidence tag}
mode=${3:-plain}
[[ $tag =~ ^[a-zA-Z0-9_-]+$ ]]
case "$mode" in
    plain) unset ASHES_RC_POISON ;;
    poison) export ASHES_RC_POISON=1 ;;
    *) exit 2 ;;
esac
export ASHES_RC_LEAKCHECK=1 UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
ulimit -s 1048576
base="work/mixed-constrained-bootstrap-$tag"
mkdir "$base"
run() {
    local budget=$1 timing=$2
    shift 2
    bash /home/mattias/.claude/jobs/heavy2.sh systemd-run --user --scope -q \
        -p "MemoryMax=$budget" -p MemorySwapMax=0 /usr/bin/time -v -o "$timing" "$@"
}
bash selfhost/tests/mixed-constrained-bootstrap/render.sh
sha256sum "$compiler" > "$base/compiler.sha256"
cp work/mixed-constrained-bootstrap/inputs.sha256 "$base/inputs.sha256"
status=0
for suite in Main SnapshotLoop; do
    cp "work/mixed-constrained-bootstrap/$suite.ash" "$base/$suite.ash"
    if ! run 8G "$base/$suite-compile.time" "$compiler" compile "$base/$suite.ash" -o "$base/$suite" \
        > "$base/$suite-compile.stdout" 2> "$base/$suite-compile.stderr"; then
        printf '%s compile failed; see %s\n' "$suite" "$base/$suite-compile.stderr" >&2
        status=1
        continue
    fi
    suite_failed=0
    for count in 1 1000 10000; do
        if ! run 4G "$base/$suite-$count.time" "$base/$suite" "$count" \
            > "$base/$suite-$count.stdout" 2> "$base/$suite-$count.stderr"; then
            suite_failed=1
            break
        fi
        case "$suite" in
            Main) expected='mixed constrained lookup passed' ;;
            SnapshotLoop) expected=$((9 * count)) ;;
        esac
        if [[ $(cat "$base/$suite-$count.stdout") != "$expected" ]]; then
            suite_failed=1
            break
        fi
        counter=$(cat "$base/$suite-$count.stderr")
        if [[ ! $counter =~ ^ashes-rc-live\ [0-9]+$ ]]; then
            suite_failed=1
            break
        fi
        printf '%s %s rounds: %s\n' "$suite" "$count" "$counter"
    done
    if (( suite_failed )) || ! cmp -s "$base/$suite-1.stderr" "$base/$suite-1000.stderr" \
        || ! cmp -s "$base/$suite-1000.stderr" "$base/$suite-10000.stderr"; then
        printf '%s failed correctness or strict lifetime checks\n' "$suite" >&2
        status=1
    else
        printf '%s passed: %s\n' "$suite" "$(cat "$base/$suite-10000.stderr")"
    fi
done
sha256sum -c "$base/inputs.sha256" || status=1
exit "$status"
