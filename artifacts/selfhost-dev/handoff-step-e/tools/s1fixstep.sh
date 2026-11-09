#!/bin/bash
# usage: s1fixstep.sh <commit>... ; for each commit, checks out it plus 57e8495f's build fix (detached), builds stage 1 and compiles work/bisect-crash.ash; returns to the branch
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
cd "$R" || exit 1
for c in "$@"; do
    git checkout -q --detach "$c" || exit 1
    git cherry-pick --no-commit 57e8495f > /dev/null || { echo "$c cherry-pick failed"; git checkout -q -f selfhost/step-e-round10; exit 1; }
    bash artifacts/selfhost-dev/handoff-step-e/tools/s1bisectstep.sh
    echo "RESULT $c code=$?"
    git checkout -q -f selfhost/step-e-round10
done
git log -1 --format=%h
echo "== FIXSTEP DONE"
