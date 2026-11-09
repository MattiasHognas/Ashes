#!/bin/bash
# usage: gathertemplates.sh ; copies every ROUNDS reproducer of this round into census-templates/, prefixed by origin
R=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
D=$R/artifacts/selfhost-dev/handoff-step-e/census-templates
rm -rf "$D"; mkdir -p "$D"
copy() {
    prefix=$1; shift
    for f in "$@"; do
        [ -f "$f" ] || continue
        grep -q "ROUNDS" "$f" || continue
        cp "$f" "$D/${prefix}_$(basename "$f")"
    done
}
copy main "$R"/artifacts/selfhost-dev/handoff-step-e/work/*.ash
copy repro "$R"/scripts/selfhost-dev/reproducers/*.ash
copy a "$R"/artifacts/selfhost-dev-agentA/repro/*.ash
copy b "$R"/artifacts/selfhost-dev-agentB/v/*.ash
copy c "$R"/artifacts/selfhost-dev-agentC/*.ash
copy d "$R"/artifacts/selfhost-dev-agentD/r/*.ash
ls "$D" | wc -l
