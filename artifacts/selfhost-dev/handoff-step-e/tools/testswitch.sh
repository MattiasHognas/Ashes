#!/bin/bash
# usage: testswitch.sh <switch-list> <Class/Test>... ; runs each C# test with no switch and under each switch; prints the switches under which a failing test passes
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
cd "$R" || exit 1
SW=$1; shift
for t in "$@"; do
    base=$(systemd-run --user --scope -q -p MemoryMax=8G -p MemorySwapMax=0 dotnet run --no-build --project src/Ashes.Tests -- --no-progress --treenode-filter "/*/*/$t" 2>&1 | grep -E '^\s*failed:' | awk '{print $2}')
    echo "== $t base failed=$base"
    for s in $(cat "$SW"); do
        f=$(env "$s=1" systemd-run --user --scope -q -p MemoryMax=8G -p MemorySwapMax=0 dotnet run --no-build --project src/Ashes.Tests -- --no-progress --treenode-filter "/*/*/$t" 2>&1 | grep -E '^\s*failed:' | awk '{print $2}')
        [ "$f" != "$base" ] && echo "   $s -> failed=$f"
    done
done
echo "== TESTSWITCH DONE"
