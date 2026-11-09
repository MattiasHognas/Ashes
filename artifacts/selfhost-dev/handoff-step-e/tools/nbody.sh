cd /home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
D=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free/artifacts/selfhost-dev/handoff-step-e/work
artifacts/selfhost-dev/cli-main/ashes compile challenges/n-body/n-body.ash -o "$D/nb-main" --emit-ir final 2> "$D/nb-main.ir" > /dev/null
artifacts/selfhost-dev/cli-rgrec/ashes compile challenges/n-body/n-body.ash -o "$D/nb-br" --emit-ir final 2> "$D/nb-br.ir" > /dev/null
norm() {
    sed -E 's/ *\([^()]*:[0-9]+:[0-9]+\)$//; s/ +$//' "$1" \
        | sed -E 's/(Target|Temp|Source|SrcTemp|DestTemp|SourceTemp|BasePtr|Left|Right|CondTemp|Slot|EnvPtrTemp|OwnerSlot|TokenTemp|ArgTemp|ClosureTemp|EnvTemp|Ptr|ValueTemp|[A-Za-z]*Slot|[A-Za-z]*Temp)=[0-9]+/\1=_/g; s/_[0-9]+:/_N:/; s/Target=[a-z_A-Z]+_[0-9]+/Target=L/; s/locals=[0-9]+ temps=[0-9]+/locals=_ temps=_/'
}
norm "$D/nb-main.ir" > "$D/nb-main.n"
norm "$D/nb-br.ir" > "$D/nb-br.n"
echo "lines main=$(wc -l < "$D/nb-main.n") branch=$(wc -l < "$D/nb-br.n") differing=$(diff "$D/nb-main.n" "$D/nb-br.n" | grep -c '^[<>]')"
diff "$D/nb-main.n" "$D/nb-br.n" | head -60
