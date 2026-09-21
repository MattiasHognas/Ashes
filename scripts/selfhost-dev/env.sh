# Sourced by every script in this directory. Defines where the repository and the scratch directory are, and
# puts in place the small inputs and scratch projects the scripts compile.
#
#   HERE  this directory, where sibling scripts and the C census sources are found
#   R     the repository root
#   J     the scratch directory (override with ASHES_SELFHOST_SCRATCH); everything the scripts build or measure
#         lands here, and it can be deleted at any time
#   T     the scratch directory's logs
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
R="$(cd "$HERE/../.." && pwd)"
J="${ASHES_SELFHOST_SCRATCH:-$R/artifacts/selfhost-dev}"
T="$J/logs"
mkdir -p "$J" "$T" "$J/tiny"

# Two programs a freshly built stage 1 is tried on: a crash shows within seconds, before anything long runs.
[ -f "$J/tiny/t0.ash" ] || printf 'Ashes.IO.print(42)\n' > "$J/tiny/t0.ash"
[ -f "$J/tiny/t4.ash" ] || printf 'let f x = x + 1\nlet g x = x + 2\nlet h x = x + 3\nlet k x = x + 4\nAshes.IO.print(f(41) + g(1) + h(1) + k(1))\n' > "$J/tiny/t4.ash"

# A scratch project compiled against this checkout's self-hosted packages. `dumpir` prints stage 1's lowered IR of
# a source file; `probe2` imports the self-hosted code generator, the module stage 1's memory is measured on.
materialize_project() {
    local name=$1
    [ -f "$J/$name/ashes.json" ] && return
    mkdir -p "$J/$name"
    cp -r "$HERE/projects/$name/." "$J/$name/"
    sed -i "s#@ROOT@#$R#g" "$J/$name/ashes.json"
    # The frontend package is pinned by the same lock the repository's own test projects carry.
    cp "$R/selfhost/tests/ir-program-parity/ashes.lock" "$J/$name/ashes.lock"
}
materialize_project dumpir
materialize_project probe2
