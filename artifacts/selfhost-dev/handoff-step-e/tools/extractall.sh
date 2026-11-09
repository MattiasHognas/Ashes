cd /home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
OUT=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free/artifacts/selfhost-dev/handoff-step-e/work/all
rm -rf "$OUT"
mkdir -p "$OUT"
for F in selfhost/tests/semantics/*.ash; do
    base=$(basename "$F" .ash)
    grep -E '^let [A-Za-z0-9]+( unit)? = ".*\\n.*"$' "$F" | while IFS= read -r line; do
        name=$(echo "$line" | sed -E 's/^let ([A-Za-z0-9]+).*/\1/')
        body=$(echo "$line" | sed -E 's/^let [A-Za-z0-9]+( unit)? = "//; s/"$//; s/\\"/"/g')
        printf '%b\n' "$body" > "$OUT/${base}__$name.ash"
    done
done
ls "$OUT" | wc -l
