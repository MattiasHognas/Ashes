#!/bin/bash
# usage: dumptoggles.sh <fixture> <SWITCH>... ; builds the stage-1 dump tool once with no switch and once under
# each environment switch (NAME, set to 1), runs it on one shared fixture and prints its exit status and output
# size: which placement rule of stage 0 makes the stage-1 binary crash.
. "$(dirname "$0")/env.sh"
name=$1; shift
D=$R/selfhost/parity/semantics/lowered-ir
cd "$R" || exit 1
cp "$D/$name.source" "$J/dumpir/$name.ash"
for toggle in none "$@"; do
    rm -rf "$J/dumpir/out"
    if [ "$toggle" = none ]; then
        dotnet run -c Release --no-build --project src/Ashes.Cli -- compile --project "$J/dumpir/ashes.json" > "$T/dumptoggle-build.log" 2>&1
    else
        env "$toggle=1" dotnet run -c Release --no-build --project src/Ashes.Cli -- compile --project "$J/dumpir/ashes.json" > "$T/dumptoggle-build.log" 2>&1
    fi
    built=$?
    BIN=$(ls "$J/dumpir/out/" 2>/dev/null | head -1)
    bash -c "ulimit -s 1048576; '$J/dumpir/out/$BIN' '$J/dumpir/$name.ash'" > "$J/dumpir/$name.toggle.s1" 2> /dev/null
    printf '%-28s build=%s run exit=%-4s lines=%s\n' "$toggle" "$built" "$?" "$(wc -l < "$J/dumpir/$name.toggle.s1")"
done
