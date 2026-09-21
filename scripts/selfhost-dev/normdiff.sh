#!/bin/bash
# usage: normdiff.sh <a.raw> <b.raw> [n] ; two lowered-IR dumps with locations stripped and numbers normalised, as a diff.
. "$(dirname "$0")/env.sh"
a=$1; b=$2; n=${3:-60}
norm() {
    sed -E 's/ *\([^()]*:[0-9]+:[0-9]+\)$//; s/ +$//' "$1" \
        | awk '/^trait evidence/{skip=1;next} skip&&/^function/{skip=0} !skip' \
        | sed -E 's/(Target|Temp|Source|SrcTemp|DestTemp|SourceTemp|BasePtr|Left|Right|CondTemp|Slot|EnvPtrTemp|OwnerSlot|TokenTemp|ArgTemp|ClosureTemp|EnvTemp|Ptr|ValueTemp|[A-Za-z]*Slot|[A-Za-z]*Temp)=[0-9]+/\1=_/g; s/_[0-9]+:/_N:/; s/Target=[a-z_A-Z]+_[0-9]+/Target=L/; s/locals=[0-9]+ temps=[0-9]+/locals=_ temps=_/'
}
norm "$a" > "$a.norm"; norm "$b" > "$b.norm"
echo "only-in-first=$(diff "$a.norm" "$b.norm" | grep -c '^<') only-in-second=$(diff "$a.norm" "$b.norm" | grep -c '^>')"
diff "$a.norm" "$b.norm" | head -"$n" | cut -c1-160
