#!/bin/bash
# usage: s1difffn.sh [-b] <fixture> <binding> ; s1diff.sh restricted to the functions lowered from one binding of a
# shared fixture: a port can be checked function by function while the rest of the fixture still differs.
. "$(dirname "$0")/env.sh"
build=""
if [ "$1" = "-b" ]; then build="-b"; shift; fi
name=$1; binding=$2
bash "$HERE/s1diff.sh" $build "$name" > /dev/null
for stage in s0 s1; do
    awk -v n="from $binding]" '/^function / { on = (index($0, n) > 0) } on' "$J/dumpir/$name.$stage.norm" > "$J/dumpir/$name.$stage.fn"
done
echo "== $name / $binding: stage0 lines=$(wc -l < "$J/dumpir/$name.s0.fn") stage1 lines=$(wc -l < "$J/dumpir/$name.s1.fn") differing=$(diff "$J/dumpir/$name.s0.fn" "$J/dumpir/$name.s1.fn" | grep -c '^[<>]')"
diff "$J/dumpir/$name.s0.fn" "$J/dumpir/$name.s1.fn" | head -"${SHOW:-30}" | cut -c1-150
