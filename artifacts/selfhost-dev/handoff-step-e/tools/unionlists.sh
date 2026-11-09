#!/bin/bash
# usage: unionlists.sh ; resolves cherry-pick conflicts in the two parity fixture lists by keeping
# both sides (ours first), adding the separating comma the .ash list needs
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
cd "$R" || exit 1
for F in selfhost/tests/ir-program-parity/Main.ash src/Ashes.Tests/SelfhostIrParityTests.cs; do
    grep -q '^<<<<<<<' "$F" || continue
    awk '
        /^<<<<<<< / { state = 1; last = ""; next }
        /^=======$/ && state == 1 {
            if (last != "" && FILENAME ~ /\.ash$/ && last !~ /,$/) { last = last "," }
            if (last != "") { print last }
            state = 2; next
        }
        /^>>>>>>> / && state == 2 { state = 0; next }
        state == 1 { if (last != "") { print last }; last = $0; next }
        { print }
    ' "$F" > "$F.resolved" && mv "$F.resolved" "$F"
    git add "$F"
done
git diff --cached --stat
