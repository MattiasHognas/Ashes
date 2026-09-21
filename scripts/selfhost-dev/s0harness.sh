#!/bin/bash
# usage: s0harness.sh [fixture]... ; stage 0 against itself: each shared fixture's stored IR, which the C# parity test
# lowers from the bare parsed program, against what the compiler's own `--emit-ir lowered` prints for the same source,
# normalised like s1diff.sh. A fixture that differs here is one where the test's lowering and the compiler's disagree,
# so a stage-1 difference against it may be the fixture's and not stage 1's. No arguments surveys every fixture.
. "$(dirname "$0")/env.sh"
D=$R/selfhost/parity/semantics/lowered-ir
cd "$R" || exit 1
E=$J/s0harness
mkdir -p "$E"
norm() {
    sed -E 's/ *\([^()]*:[0-9]+:[0-9]+\)$//; s/ +$//' "$1" \
        | awk '/^trait evidence/{skip=1;next} skip&&/^function/{skip=0} !skip' \
        | sed -E 's/(Target|Temp|Source|SrcTemp|DestTemp|SourceTemp|BasePtr|Left|Right|CondTemp|Slot|EnvPtrTemp|OwnerSlot|TokenTemp|ArgTemp|ClosureTemp|EnvTemp|Ptr|ValueTemp|[A-Za-z]*Slot|[A-Za-z]*Temp)=[0-9]+/\1=_/g; s/_[0-9]+:/_N:/; s/Target=[a-z_A-Z]+_[0-9]+/Target=L/; s/locals=[0-9]+ temps=[0-9]+/locals=_ temps=_/'
}
names=("$@")
if [ ${#names[@]} -eq 0 ]; then
    for f in "$D"/*.source; do names+=("$(basename "$f" .source)"); done
fi
for name in "${names[@]}"; do
    cp "$D/$name.source" "$E/$name.ash"
    dotnet run -c Release --no-build --project src/Ashes.Cli -- compile "$E/$name.ash" -o "$E/bin" --emit-ir lowered > /dev/null 2> "$E/$name.cli"
    norm "$D/$name.ir" > "$E/$name.fixture.norm"
    norm "$E/$name.cli" > "$E/$name.cli.norm"
    differing=$(diff "$E/$name.fixture.norm" "$E/$name.cli.norm" | grep -c '^[<>]')
    [ "$differing" -ne 0 ] && echo "== $name: fixture and compiler differ in $differing lines"
done
echo "harness survey done: ${#names[@]} fixtures"
