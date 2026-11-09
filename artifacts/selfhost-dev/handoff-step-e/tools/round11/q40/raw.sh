#!/bin/bash
# raw.sh <dumpdir> <hex-header-addr> [words] : raw 8-byte words of a cell from its header
a=$((0x$2)); base=$((0x100000000000)); k=$(( (a-base) / 1073741824 )); off=$(( a - base - k*1073741824 ))
od -A x -t x8 -j $off -N $(( ${3:-6} * 8 )) $1/full-$k.bin
