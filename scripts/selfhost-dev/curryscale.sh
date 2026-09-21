#!/bin/bash
# usage: curryscale.sh <stage1-binary> <k>... ; compiles a program with one k-parameter curried function
# whose body calls another function (so call results resolve during lowering) and prints wall time, peak
# RSS and exit status per k. Exponential growth in k means the body is re-lowered once per nesting level.
. "$(dirname "$0")/env.sh"
bin=$1; shift
for k in "$@"; do
    params=""; sum="0"; args=""
    for i in $(seq 1 "$k"); do params="$params p$i"; sum="$sum + p$i"; args="$args(1)"; done
    {
        echo "let helper x = x + 1"
        echo "let many$params = helper($sum) |> (given y -> y + helper(p1))"
        echo "Ashes.IO.print(many$args)"
    } > "$J/tiny/curry_$k.ash"
    rm -f "$J/tiny.out"
    start=$(date +%s.%N)
    /usr/bin/time -f '%M' -o "$J/curry.rss" bash -c "ulimit -s 1048576; '$bin' compile '$J/tiny/curry_$k.ash' -o '$J/tiny.out'" > "$J/curry.log" 2>&1
    code=$?
    end=$(date +%s.%N)
    printf 'k=%-3s exit=%-3s time=%6.2fs peak=%6s MB  prints=%s\n' "$k" "$code" "$(awk -v a="$start" -v b="$end" 'BEGIN{print b-a}')" "$(awk '{printf "%.0f", $1/1024}' "$J/curry.rss")" "$([ -x "$J/tiny.out" ] && "$J/tiny.out" 2>&1 | head -1)"
done
