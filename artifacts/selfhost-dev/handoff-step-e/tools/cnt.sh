S=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free/artifacts/selfhost-dev/handoff-step-e/work/src
n=$1
fn=$2
shift 2
awk -v f="$fn" '/^function /{p=(index($0, f) > 0)} p' "$S/$n.s1" > "$S/$n.fn"
while [ $# -gt 0 ]; do
    a=$1
    b=$2
    shift 2
    if [ -z "$b" ]; then
        echo "[$a] = $(grep -c -F -- "$a" "$S/$n.fn")"
    else
        echo "[$a & $b] = $(grep -F -- "$a" "$S/$n.fn" | grep -c -F -- "$b")"
    fi
done
