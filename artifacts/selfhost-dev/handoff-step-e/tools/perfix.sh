#!/bin/bash
# usage: perfix.sh ; the per-fix check of step E: the six self-hosted suites, then whole-program IR parity
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
cd "$R" || exit 1
dotnet build -c Release src/Ashes.Cli 2>&1 | grep -E "rror\(s\)"
bash scripts/selfhost-dev/suites.sh
bash scripts/selfhost-dev/parityonly.sh 2>&1 | tail -5
