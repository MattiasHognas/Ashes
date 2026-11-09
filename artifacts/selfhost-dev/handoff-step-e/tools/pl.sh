#!/bin/bash
# usage: pl.sh <work-name> <rounds>... ; plateau.sh on work/<work-name>.ash
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
n=$1; shift
echo "== $n"
bash "$R/scripts/selfhost-dev/plateau.sh" "$R/artifacts/selfhost-dev/handoff-step-e/work/$n.ash" "$@"
