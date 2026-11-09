#!/bin/bash
# usage: keepbothlines.sh <file>... ; resolves add/add cherry-pick conflicts whose two sides are
# independent line additions (template registrations, test lists) by keeping ours then theirs
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
cd "$R" || exit 1
for F in "$@"; do
    sed -i -e '/^<<<<<<< /d' -e '/^=======$/d' -e '/^>>>>>>> /d' "$F"
    git add "$F"
done
