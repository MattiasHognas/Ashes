#!/bin/bash
# usage: census.sh <region.bin> <linkmap> <out-dir> [roots-dir]
# Exit census of a region dump (exitdump.sh): every live RC cell counted once, under its size
# class and shape and under the function that allocated it (a program built with
# ASHES_RC_SITES=1; "?" for every cell otherwise). Writes by-shape.txt and by-site.txt, each line
# "<cells> <bytes> <key> <share of cells> <share of bytes> <unreachable cells> <unreachable bytes>
# <leak roots>", by-site-named.txt with the function's origin from <linkmap>.names when that file
# exists, and total.txt.
# With the roots exitdump.sh writes (roots-dir, by default the roots directory beside the dump)
# rcmark marks every cell reachable from them. An unreachable cell is a sure leak and a reachable
# one at most still held at exit, since the mark is conservative; a leak root is an unreachable
# cell no other unreachable cell names, the top of a leaked structure, and its site is where the
# missing release belongs. Without roots the three columns are 0 and mean nothing.
T=$(dirname "$(realpath "$0")"); dump=$1; map=$2; out=$3; roots=${4:-$(dirname "$dump")/roots}
for tool in rcwalk rcmark rcsites; do [ -x "$T/$tool" ] || gcc -O2 -o "$T/$tool" "$T/$tool.c" || exit 1; done
export LC_ALL=C
mkdir -p "$out"
if [ -d "$roots" ]; then walk=("$T/rcmark" "$dump" "$roots"); else walk=("$T/rcwalk" "$dump"); fi
tally='{k = key == "shape" ? $1"_"$2 : $3; c[k]++; b[k] += $1; t++; tb += $1; if ($5 == "U" || $5 == "L") {u[k]++; ub[k] += $1; tu++; tub += $1} if ($5 == "L") {l[k]++; tl++}}
  END {for (k in c) printf "%d %d %s %.2f%% %.2f%% %d %d %d\n", c[k], b[k], k, 100*c[k]/t, 100*b[k]/tb, u[k], ub[k], l[k]; printf "TOTAL %d cells %d bytes, unreachable %d cells %d bytes, %d leak roots\n", t, tb, tu, tub, tl > total}'
"${walk[@]}" 2> "$out/walk.log" | "$T/rcsites" "$map" \
  | tee >(awk -v key=shape -v total="$out/total.txt" "$tally" | sort -k1 -n -r > "$out/by-shape.txt") \
  | awk -v key=site -v total=/dev/null "$tally" | sort -k1 -n -r > "$out/by-site.txt"
sleep 1
[ -f "$map.names" ] && awk 'NR==FNR {split($0, f, "\t"); n[f[1]]=f[3]; next} {print $0, ($3 in n ? n[$3] : "")}' "$map.names" "$out/by-site.txt" > "$out/by-site-named.txt"
[ -f "$(dirname "$dump")/roots.log" ] && cat "$(dirname "$dump")/roots.log"
cat "$out/walk.log" "$out/total.txt"; head -30 "$out/by-shape.txt"; echo; head -30 "$out/by-site.txt"
