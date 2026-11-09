R=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
J=$R/artifacts/selfhost-dev
# usage: s1at.sh <commit> [stage0-cli] ; builds stage 1 from <commit>'s selfhost/ and lib/ trees with a fixed stage 0
# (default: this worktree's Release CLI) and runs probepeak.sh on it. Bisects a stage-1 source regression.
commit=$1
CLI=${2:-$R/src/Ashes.Cli/bin/Release/net10.0/ashes}
D=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free/artifacts/selfhost-dev/handoff-step-e/work/s1src-$commit
rm -rf "$D"
mkdir -p "$D"
cd "$R"
git archive "$commit" selfhost lib | tar -x -C "$D"
cd "$D"
"$CLI" compile --project selfhost/packages/cli/ashes.json > "$D/build.log" 2>&1
echo "build $commit exit=$?"
BIN=$(ls selfhost/packages/cli/out/ 2>/dev/null | head -1)
[ -z "$BIN" ] && { tail -3 "$D/build.log"; exit 1; }
cp "selfhost/packages/cli/out/$BIN" "$J/s1-at-$commit"
cd "$R"
bash scripts/selfhost-dev/probepeak.sh "$J/s1-at-$commit" | tail -1
rm -rf "$D"
