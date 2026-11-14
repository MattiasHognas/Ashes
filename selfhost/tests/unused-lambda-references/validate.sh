#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../../.."
ulimit -s 1048576
base=work/unused-lambda-references
for flavor in plain poison; do
    for rounds in 1000 10000; do
        stem="$base/plateau-$flavor-$rounds"
        systemd-run --user --scope -q -p MemoryMax=4G -p MemorySwapMax=0 \
            /usr/bin/time -v -o "$stem.time" "$base/plateau-$flavor" "$rounds" \
            > "$stem.stdout" 2> "$stem.stderr"
        [[ $(cat "$stem.stdout") == 'unused lambda reference coverage passed' ]]
        counter=$(cat "$stem.stderr")
        if [[ ! "$counter" =~ ^ashes-rc-live\ [0-9]+$ ]]; then
            printf 'Missing or unexpected live-cell counter: %s\n' "$stem.stderr" >&2
            exit 1
        fi
    done
    cmp "$base/plateau-$flavor-1000.stderr" "$base/plateau-$flavor-10000.stderr"
done
printf 'Reference coverage and strict live-cell plateaus passed.\n'
