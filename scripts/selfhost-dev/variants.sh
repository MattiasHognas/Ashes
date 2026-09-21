#!/bin/bash
# usage: variants.sh <template.ash> <rounds> <name>=<sed-expression>... ; applies each sed expression to the
# template, one variant at a time, and runs plateau.sh on the result: shrinking a reproducer by deleting or
# replacing one construct per variant, without keeping a file per attempt.
. "$(dirname "$0")/env.sh"
t=$1; n=$2; shift 2
V=$J/variants
mkdir -p "$V"
printf '%-22s ' "unchanged"; bash "$HERE/plateau.sh" "$t" "$n"
for spec in "$@"; do
    name=${spec%%=*}; expr=${spec#*=}
    sed -E "$expr" "$t" > "$V/$name.ash"
    printf '%-22s ' "$name"; bash "$HERE/plateau.sh" "$V/$name.ash" "$n"
done
