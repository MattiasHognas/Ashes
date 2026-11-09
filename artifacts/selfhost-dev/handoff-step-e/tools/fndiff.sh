D=/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free/artifacts/selfhost-dev/handoff-step-e/work
a=$1
b=$2
awk '/^function /{name=$0; sub(/^function [^ ]+ +/, "", name)} {print name "\t" $0}' "$a" | sed -E 's/__(rcnorm|rcdrop_record|deepcopy|deepcopy_list|rcdrop)_[0-9]+/__\1_N/g; s/lambda_[0-9]+/lambda_N/g' > "$D/fa.tsv"
awk '/^function /{name=$0; sub(/^function [^ ]+ +/, "", name)} {print name "\t" $0}' "$b" | sed -E 's/__(rcnorm|rcdrop_record|deepcopy|deepcopy_list|rcdrop)_[0-9]+/__\1_N/g; s/lambda_[0-9]+/lambda_N/g' > "$D/fb.tsv"
cut -f1 "$D/fa.tsv" | sort -u > "$D/na"
cut -f1 "$D/fb.tsv" | sort -u > "$D/nb"
echo "only in A:"; comm -23 "$D/na" "$D/nb"
echo "only in B:"; comm -13 "$D/na" "$D/nb"
echo "changed:"
comm -12 "$D/na" "$D/nb" | while IFS= read -r fn; do
    ca=$(grep -F "$fn	" "$D/fa.tsv" | md5sum)
    cb=$(grep -F "$fn	" "$D/fb.tsv" | md5sum)
    [ "$ca" != "$cb" ] && echo "  $fn"
done
