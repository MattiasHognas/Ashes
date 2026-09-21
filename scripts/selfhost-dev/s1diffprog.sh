#!/bin/bash
# usage: s1diffprog.sh [-b] <program.ash> [binding] ; stage 0's lowered IR of any program against stage 1's, normalised
# like s1diff.sh, whole or for the functions lowered from one binding: a port is driven by the smallest program that
# shows the rule, before a fixture exists. -b rebuilds the dump tool first.
. "$(dirname "$0")/env.sh"
cd "$R" || exit 1
if [ "$1" = "-b" ]; then
    shift
    rm -rf "$J/dumpir/out"
    dotnet run -c Release --no-build --project src/Ashes.Cli -- compile --project "$J/dumpir/ashes.json" > "$T/dumpir-build.log" 2>&1
    echo "dump tool build exit=$? $(grep -E 'ASH[0-9]+|rror' "$T/dumpir-build.log" | head -3 | cut -c1-240)"
fi
src=$(readlink -f "$1"); binding=$2
name=$(basename "$src" .ash)
BIN=$(ls "$J/dumpir/out/" 2>/dev/null | head -1)
[ -z "$BIN" ] && { echo "no dump tool binary; run with -b"; exit 1; }
E=$J/diffprog
mkdir -p "$E"
norm() {
    sed -E 's/ *\([^()]*:[0-9]+:[0-9]+\)$//; s/ +$//' "$1" \
        | awk '/^trait evidence/{skip=1;next} skip&&/^function/{skip=0} !skip' \
        | sed -E 's/(Target|Temp|Source|SrcTemp|DestTemp|SourceTemp|BasePtr|Left|Right|CondTemp|Slot|EnvPtrTemp|OwnerSlot|TokenTemp|ArgTemp|ClosureTemp|EnvTemp|Ptr|ValueTemp|[A-Za-z]*Slot|[A-Za-z]*Temp)=[0-9]+/\1=_/g; s/_[0-9]+:/_N:/; s/Target=[a-z_A-Z]+_[0-9]+/Target=L/; s/locals=[0-9]+ temps=[0-9]+/locals=_ temps=_/'
}
dotnet run -c Release --no-build --project src/Ashes.Cli -- compile "$src" -o "$E/bin" --emit-ir lowered > /dev/null 2> "$E/$name.s0"
echo "stage 0 exit=$?"
cp "$src" "$J/dumpir/$name.ash"
bash -c "ulimit -s 1048576; '$J/dumpir/out/$BIN' '$J/dumpir/$name.ash'" > "$E/$name.s1" 2>&1
echo "stage 1 exit=$?"
for stage in s0 s1; do
    if [ -n "$binding" ]; then
        norm "$E/$name.$stage" | awk -v n="from $binding]" '/^function / { on = (index($0, n) > 0) } on' > "$E/$name.$stage.norm"
    else
        norm "$E/$name.$stage" > "$E/$name.$stage.norm"
    fi
done
echo "== $name ${binding:+/ $binding}: stage0 lines=$(wc -l < "$E/$name.s0.norm") stage1 lines=$(wc -l < "$E/$name.s1.norm") differing=$(diff "$E/$name.s0.norm" "$E/$name.s1.norm" | grep -c '^[<>]')"
diff "$E/$name.s0.norm" "$E/$name.s1.norm" | head -"${SHOW:-40}" | cut -c1-150
