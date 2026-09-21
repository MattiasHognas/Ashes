#!/bin/bash
# usage: s1rawside.sh <program-name> <from-line> <to-line> ; after s1rawprog.sh, the two raw dumps side by side over
# a line range: where a numbering difference starts, and which allocation one stage makes that the other does not.
. "$(dirname "$0")/env.sh"
name=$1; from=$2; to=$3
E=$J/diffprog
sed -n "${from},${to}p" "$E/$name.s0.raw" | cut -c1-92 > "$E/side.s0"
sed -n "${from},${to}p" "$E/$name.s1.raw" | cut -c1-92 > "$E/side.s1"
diff -y -W 190 "$E/side.s0" "$E/side.s1"
