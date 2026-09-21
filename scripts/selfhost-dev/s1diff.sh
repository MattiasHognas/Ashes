#!/bin/bash
# usage: s1diff.sh [-b] <fixture>... ; stage 1's lowered IR for each shared fixture against the stage-0 fixture,
# with locations stripped and temp/slot/label numbers normalised, so only the instructions that differ show.
# -b rebuilds the dump tool from the worktree's stage-1 sources first (needed after editing them).
. "$(dirname "$0")/env.sh"
D=$R/selfhost/parity/semantics/lowered-ir
cd "$R" || exit 1
if [ "$1" = "-b" ]; then
    shift
    rm -rf "$J/dumpir/out"
    dotnet run -c Release --no-build --project src/Ashes.Cli -- compile --project "$J/dumpir/ashes.json" > "$T/dumpir-build.log" 2>&1
    echo "dump tool build exit=$? $(grep -E 'ASH[0-9]+|rror' "$T/dumpir-build.log" | head -3 | cut -c1-240)"
fi
BIN=$(ls "$J/dumpir/out/" 2>/dev/null | head -1)
[ -z "$BIN" ] && { echo "no dump tool binary; run with -b"; exit 1; }
norm() {
    sed -E 's/ *\([^()]*:[0-9]+:[0-9]+\)$//; s/ +$//' "$1" \
        | awk '/^trait evidence/{skip=1;next} skip&&/^function/{skip=0} !skip' \
        | sed -E 's/(Target|Temp|Source|SrcTemp|DestTemp|SourceTemp|BasePtr|Left|Right|CondTemp|Slot|EnvPtrTemp|OwnerSlot|TokenTemp|ArgTemp|ClosureTemp|EnvTemp|Ptr|ValueTemp|[A-Za-z]*Slot|[A-Za-z]*Temp)=[0-9]+/\1=_/g; s/_[0-9]+:/_N:/; s/Target=[a-z_A-Z]+_[0-9]+/Target=L/; s/locals=[0-9]+ temps=[0-9]+/locals=_ temps=_/'
}
for name in "$@"; do
    cp "$D/$name.source" "$J/dumpir/$name.ash"
    bash -c "ulimit -s 1048576; '$J/dumpir/out/$BIN' '$J/dumpir/$name.ash'" > "$J/dumpir/$name.s1" 2>&1
    norm "$D/$name.ir" > "$J/dumpir/$name.s0.norm"
    norm "$J/dumpir/$name.s1" > "$J/dumpir/$name.s1.norm"
    echo "== $name: stage0-only lines=$(diff "$J/dumpir/$name.s0.norm" "$J/dumpir/$name.s1.norm" | grep -c '^<') stage1-only lines=$(diff "$J/dumpir/$name.s0.norm" "$J/dumpir/$name.s1.norm" | grep -c '^>')"
    diff "$J/dumpir/$name.s0.norm" "$J/dumpir/$name.s1.norm" | head -"${SHOW:-40}" | cut -c1-160
done
