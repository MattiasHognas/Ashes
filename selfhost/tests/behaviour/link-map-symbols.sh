#!/usr/bin/env bash
# Turns a link map (ASHES_LINK_MAP) into a small ELF file that holds only symbols at the mapped
# addresses, so gdb names functions in a stage-1-linked executable:
#   gdb -ex 'add-symbol-file <map>.sym.elf' --args <executable> ...
# The executable itself is not touched. Needs `as` and `ld` (binutils).
set -uo pipefail

if [[ $# -lt 1 || $1 == -h || $1 == --help ]]; then
    echo "Usage: bash selfhost/tests/behaviour/link-map-symbols.sh <map> [<output elf>]" >&2
    echo "       (default output: <map>.sym.elf)" >&2
    exit 2
fi
map=$1
output=${2:-$map.sym.elf}
if [[ ! -s $map ]]; then
    echo "link-map-symbols: empty or missing map $map" >&2
    exit 2
fi

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# One NOBITS section at the map's first address; each function becomes a sized function symbol at
# its offset. Names are quoted, since code labels may contain `$`.
awk '
    NR == 1 {
        base = strtonum("0x" $1)
        printf "%s\n", "    .section .ashes_text,\"ax\",@nobits"
    }
    NF >= 3 {
        offset = strtonum("0x" $1) - base
        if (offset < last) next
        name = $3
        if (name in seen) name = name "@" $1
        seen[name] = 1
        last = offset
        printf "    .org 0x%x\n    .globl \"%s\"\n    .type \"%s\", @function\n\"%s\":\n    .size \"%s\", 0x%s\n", offset, name, name, name, name, $2
    }
' "$map" > "$work/symbols.s"
base=$(awk 'NR == 1 { print "0x" $1; exit }' "$map")

if ! as -o "$work/symbols.o" "$work/symbols.s"; then
    echo "link-map-symbols: as failed" >&2
    exit 1
fi
if ! ld -o "$output" --section-start=.ashes_text="$base" -e "$base" --no-warn-rwx-segments "$work/symbols.o"; then
    echo "link-map-symbols: ld failed" >&2
    exit 1
fi
echo "$output"
