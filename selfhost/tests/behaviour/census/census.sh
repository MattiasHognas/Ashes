#!/bin/bash
# usage: census.sh <region.bin> <linkmap> <out-dir>
# Exit census of a region dump (exitdump.sh): every live RC cell counted once, under its size
# class and shape and under the function that allocated it (a program built with
# ASHES_RC_SITES=1; "?" for every cell otherwise). Writes by-shape.txt and by-site.txt, each line
# "<cells> <bytes> <key> <share of cells> <share of bytes>", by-site-named.txt with the function's
# origin from <linkmap>.names when that file exists, and total.txt.
T=$(dirname "$(realpath "$0")"); dump=$1; map=$2; out=$3
for tool in rcwalk rcsites; do [ -x "$T/$tool" ] || gcc -O2 -o "$T/$tool" "$T/$tool.c" || exit 1; done
export LC_ALL=C
mkdir -p "$out"
tally='{k = key == "shape" ? $1"_"$2 : $3; c[k]++; b[k] += $1; t++; tb += $1}
  END {for (k in c) printf "%d %d %s %.2f%% %.2f%%\n", c[k], b[k], k, 100*c[k]/t, 100*b[k]/tb; printf "TOTAL %d cells %d bytes\n", t, tb > total}'
"$T/rcwalk" "$dump" 2> "$out/walk.log" | "$T/rcsites" "$map" \
  | tee >(awk -v key=shape -v total="$out/total.txt" "$tally" | sort -k1 -n -r > "$out/by-shape.txt") \
  | awk -v key=site -v total=/dev/null "$tally" | sort -k1 -n -r > "$out/by-site.txt"
sleep 1
[ -f "$map.names" ] && awk 'NR==FNR {n[$1]=$3; next} {print $0, ($3 in n ? n[$3] : "")}' "$map.names" "$out/by-site.txt" > "$out/by-site-named.txt"
cat "$out/walk.log" "$out/total.txt"; head -30 "$out/by-shape.txt"; echo; head -30 "$out/by-site.txt"
