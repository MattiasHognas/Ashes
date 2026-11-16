#!/usr/bin/env bash
# Clusters the `stage1-crash` rows of a behavioural results file by the function each crash
# happened in: reruns every crashing compile under gdb (crash-trace.sh --brief), resolves the
# crash's key PC through the compiler's link map, and prints one row per function, most crashes
# first, with three example tests.
set -uo pipefail

usage() {
    cat <<'EOF'
Usage: bash selfhost/tests/behaviour/cluster-crashes.sh --compiler <path> --results <tsv>
           [--map <map>] [--out <dir>] [--jobs N] [--timeout S] [--memory M] [names...]

  --compiler <path>  the crashing compiler (a stage-1-linked executable)
  --results <tsv>    behavioural results (`name<TAB>bucket<TAB>detail`); its stage1-crash rows
                     are rerun as `<compiler> compile tests/<name>.ash -o <out>/bin/<name>`
                     from the repository root
  --map <map>        the compiler's link map; default <compiler>.map when it exists. Without a
                     map the crashes are clustered by raw key PC instead of by function
  --out <dir>        default work/crash-clusters/<timestamp>; receives crashes.tsv (one row per
                     test: symbol, name, signal, crash PC, key PC, key source) and clusters.txt
  --jobs N           runs in parallel (default 4)
  --timeout S        seconds per run (default 60)
  --memory M         memory cap per run (default 4G)
  names...           rerun only these tests (still taken from the stage1-crash rows)

The key PC is the crash PC when it lies in the compiler's code, else the innermost gdb frame in
the code, else the first candidate return address on the stack (see crash-trace.sh); the
`source` column says which, and `stack` keys are approximate.
EOF
}

script_dir=$(dirname "$(realpath "${BASH_SOURCE[0]}")")
repo_root=$(realpath "$script_dir/../../..")
compiler=""
results=""
map=""
out=""
jobs=4
seconds=60
memory=4G
while [[ $# -gt 0 ]]; do
    case "$1" in
        --compiler) compiler=$(realpath "$2"); shift 2 ;;
        --results) results=$(realpath "$2"); shift 2 ;;
        --map) map=$(realpath "$2"); shift 2 ;;
        --out) out=$2; shift 2 ;;
        --jobs) jobs=$2; shift 2 ;;
        --timeout) seconds=$2; shift 2 ;;
        --memory) memory=$2; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        --*) echo "cluster-crashes: unknown option $1" >&2; usage; exit 2 ;;
        *) break ;;
    esac
done
if [[ -z $compiler || -z $results ]]; then
    usage
    exit 2
fi
if [[ -z $map && -r "$compiler.map" ]]; then
    map="$compiler.map"
fi
if [[ -z $out ]]; then
    out="$repo_root/work/crash-clusters/$(date +%Y%m%d-%H%M%S)"
fi
mkdir -p "$out/rows" "$out/bin"
out=$(realpath "$out")

if [[ $# -gt 0 ]]; then
    names=$(printf '%s\n' "$@" | awk -F'\t' 'NR == FNR { wanted[$1] = 1; next } $2 == "stage1-crash" && wanted[$1] { print $1 }' - "$results")
else
    names=$(awk -F'\t' '$2 == "stage1-crash" { print $1 }' "$results")
fi
total=$(printf '%s\n' "$names" | grep -c . || true)
echo "compiler: $compiler"
echo "map: ${map:-none (clustering by raw PC)}"
echo "crashing tests: $total, jobs: $jobs, out: $out"

# One rerun: writes rows/<name>.tsv as `name<TAB>signal<TAB>crash pc<TAB>key pc<TAB>source`.
# A run that ends any other way (timeout, memory cap) gets its exit code as the signal column.
run_one() {
    local name=$1 line code
    ulimit -c 0
    ulimit -s 1048576
    line=$(cd "$repo_root" && systemd-run --user --scope -q -p "MemoryMax=$memory" -p MemorySwapMax=0 \
        -p "RuntimeMaxSec=$((seconds + 30))" timeout -k 5 "$seconds" \
        bash "$script_dir/crash-trace.sh" --brief ${map:+--map "$map"} -- \
        "$compiler" compile "tests/$name.ash" -o "$out/bin/$name")
    code=$?
    rm -f "$out/bin/$name"
    if [[ -z $line ]]; then
        line=$(printf 'exit-%s\t-\t-\t-' "$code")
    fi
    printf '%s\t%s\n' "$name" "$line" > "$out/rows/$name.tsv"
}
export -f run_one
export repo_root script_dir compiler map out seconds memory

start=$(date +%s)
printf '%s\n' "$names" | grep . | xargs -P "$jobs" -I{} bash -c 'run_one "$1"' _ {}
echo "reruns took $(( $(date +%s) - start ))s"

: > "$out/crashes.tsv"
for name in $names; do
    if [[ -r "$out/rows/$name.tsv" ]]; then
        cat "$out/rows/$name.tsv" >> "$out/crashes.tsv"
    fi
done

# Prepends the cluster key: the key PC's symbol through the map, or the raw key PC without one.
if [[ -n $map ]]; then
    bash "$script_dir/resolve-address.sh" "$map" --column 4 < "$out/crashes.tsv" > "$out/crashes.keyed.tsv"
else
    awk -F'\t' -v OFS='\t' '{ print ($4 == "-" ? "-" : $4), $0 }' "$out/crashes.tsv" > "$out/crashes.keyed.tsv"
fi
mv "$out/crashes.keyed.tsv" "$out/crashes.tsv"

{
    printf '%-60s %7s  %s\n' "symbol (key PC; source pc/frame exact, stack approximate)" "crashes" "examples"
    awk -F'\t' '
        {
            key = $1
            if ($1 == "-") key = "(no key: " $3 ")"
            count[key]++
            sources[key, $6]++
            if (count[key] <= 3) examples[key] = examples[key] (count[key] > 1 ? ", " : "") $2
        }
        END {
            for (key in count) {
                mix = ""
                if (sources[key, "pc"]) mix = mix " pc:" sources[key, "pc"]
                if (sources[key, "frame"]) mix = mix " frame:" sources[key, "frame"]
                if (sources[key, "stack"]) mix = mix " stack:" sources[key, "stack"]
                printf "%d\t%s%s\t%s\n", count[key], key, (mix == "" ? "" : " [" substr(mix, 2) "]"), examples[key]
            }
        }' "$out/crashes.tsv" | sort -t $'\t' -k1,1nr -k2,2 | awk -F'\t' '{ printf "%-60s %7d  %s\n", $2, $1, $3 }'
    echo
    echo "by signal:"
    awk -F'\t' '{ print $3 }' "$out/crashes.tsv" | sort | uniq -c | sort -nr
} > "$out/clusters.txt"
cat "$out/clusters.txt"
