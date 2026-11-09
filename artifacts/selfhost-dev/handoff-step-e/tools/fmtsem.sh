#!/bin/bash
# usage: fmtsem.sh <file>... ; canonical format of each semantics source file named (relative to the Semantics folder)
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
S=$R/selfhost/packages/semantics/src/AshesCompiler/Semantics
for f in "$@"; do
    dotnet run -c Release --no-build --project "$R/src/Ashes.Cli" -- fmt "$S/$f" -w | tail -1
done
