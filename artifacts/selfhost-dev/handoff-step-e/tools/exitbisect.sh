#!/bin/bash
cd /home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
git bisect start 8f910f16 a1b4d82d
git bisect run bash artifacts/selfhost-dev/handoff-step-e/tools/exitbisectstep.sh
git bisect log
git bisect reset
git log -1 --format=%h
echo "== EXIT BISECT DONE"
