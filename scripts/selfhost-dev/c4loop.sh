#!/bin/bash
# The C4 inner loop: rebuild the stage-1 dump tool, run whole-program parity, and print the instruction-level
# difference size of every program that still differs. SHOW=n also prints the first n diff lines of each.
. "$(dirname "$0")/env.sh"
bash "$HERE/s1diff.sh" -b > "$T/c4loop-build.log" 2>&1
grep "dump tool build" "$T/c4loop-build.log"
bash "$HERE/parityonly.sh" | grep -E "fixtures|build exit=[1-9]" | cut -c1-160
bash "$HERE/differing.sh"
