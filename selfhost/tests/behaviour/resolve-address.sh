#!/usr/bin/env bash
# Resolves code addresses of a stage-1-linked executable to `symbol+offset` through the link map
# the self-hosted compiler writes when ASHES_LINK_MAP is set (one line per function,
# `<hex address> <hex size> <name>`, sorted by address).
set -uo pipefail

usage() {
    cat <<'EOF'
Usage: bash selfhost/tests/behaviour/resolve-address.sh <map> <address>...
       bash selfhost/tests/behaviour/resolve-address.sh <map> --column N < lines

  <address>     hexadecimal, with or without 0x; prints `<address> <symbol>+0x<offset>`
  --column N    reads tab-separated lines from stdin and prints each one with a new first
                column holding the symbol (without offset) of the address in column N

An address below the first symbol or past the end of the last one resolves to `?`. An address
past the recorded size of its symbol (size 0 means unknown) is marked `(past end)`.
EOF
}

if [[ $# -lt 2 || $1 == -h || $1 == --help ]]; then
    usage
    exit 2
fi

map=$1
shift
if [[ ! -r $map ]]; then
    echo "resolve-address: cannot read map $map" >&2
    exit 2
fi

resolver='
function hex(text) {
    if (text !~ /^0[xX]/) text = "0x" text
    return strtonum(text)
}
function lookup(address,    low, high, mid) {
    if (count == 0 || address < start[1]) return 0
    low = 1
    high = count
    while (low < high) {
        mid = int((low + high + 1) / 2)
        if (start[mid] <= address) low = mid
        else high = mid - 1
    }
    return low
}
function describe(address, withOffset,    index_, offset, text) {
    index_ = lookup(address)
    if (index_ == 0) return "?"
    offset = address - start[index_]
    if (index_ == count && size[index_] > 0 && offset >= size[index_]) return "?"
    text = name[index_]
    if (withOffset) text = text sprintf("+0x%x", offset)
    if (size[index_] > 0 && offset >= size[index_]) text = text " (past end)"
    return text
}
FNR == NR {
    if (NF >= 3) {
        count++
        start[count] = hex($1)
        size[count] = hex($2)
        name[count] = $3
    }
    next
}
mode == "column" {
    split($0, parts, "\t")
    value = parts[column]
    if (value == "" || value == "-") print "-\t" $0
    else print describe(hex(value), 0) "\t" $0
    next
}
{
    print $1 " " describe(hex($1), 1)
}
'

# The map in address order, whatever order it was written in.
sorted_map() {
    awk 'NF >= 3 { printf "%016x %s\n", strtonum("0x" $1), $0 }' "$map" | LC_ALL=C sort | cut -d " " -f 2-
}

if [[ $1 == --column ]]; then
    column=${2:?--column needs a number}
    awk -v mode=column -v column="$column" "$resolver" <(sorted_map) -
else
    printf '%s\n' "$@" | awk -v mode=single "$resolver" <(sorted_map) -
fi
