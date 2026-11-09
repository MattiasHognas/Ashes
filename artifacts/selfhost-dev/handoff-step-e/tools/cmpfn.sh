cd /home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free
D=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free/artifacts/selfhost-dev/handoff-step-e/work
name=$1
fn=$2
dotnet run -c Release --no-build --project src/Ashes.Cli -- compile "$D/$name.ash" -o "$D/$name" --emit-ir lowered 2> "$D/$name.s0.ir" > /dev/null
echo "stage0 exit=$? run=$("$D/$name")"
BIN=$(ls artifacts/selfhost-dev/dumpir/out/ | head -1)
bash -c "ulimit -s 1048576; artifacts/selfhost-dev/dumpir/out/$BIN $D/$name.ash" > "$D/$name.s1.ir" 2>&1
echo "stage1 dump exit=$?"
norm() {
    awk -v f="$fn" '/^function /{p=(index($0, f) > 0)} p' "$1" \
        | sed -E 's/ *\([^()]*:[0-9]+:[0-9]+\)$//; s/ +$//' \
        | sed -E 's/(Target|Temp|Source|SrcTemp|DestTemp|SourceTemp|BasePtr|Left|Right|CondTemp|Slot|EnvPtrTemp|OwnerSlot|TokenTemp|ArgTemp|ClosureTemp|EnvTemp|Ptr|ValueTemp|[A-Za-z]*Slot|[A-Za-z]*Temp)=[0-9]+/\1=_/g; s/_[0-9]+:/_N:/; s/Target=[a-z_A-Z]+_[0-9]+/Target=L/; s/locals=[0-9]+ temps=[0-9]+/locals=_ temps=_/; s/__(rcnorm|rcdrop_record|deepcopy)_[0-9]+/__\1_N/g'
}
norm "$D/$name.s0.ir" > "$D/$name.f0"
norm "$D/$name.s1.ir" > "$D/$name.f1"
echo "fn lines s0=$(wc -l < "$D/$name.f0") s1=$(wc -l < "$D/$name.f1") differing=$(diff "$D/$name.f0" "$D/$name.f1" | grep -c '^[<>]')"
diff "$D/$name.f0" "$D/$name.f1" | head -${SHOW:-30}
