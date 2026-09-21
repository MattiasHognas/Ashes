#!/bin/bash
# usage: tinypeek.sh <depth> <address-hex>... ; prints cells of the dump the last tinycensus.sh left as trees of
# their payload words (rcpeek): what a leaked root found by a census actually holds.
. "$(dirname "$0")/env.sh"
census_tool rcpeek || exit 1
depth=$1; shift
"$T/rcpeek" "$T/tiny-dump" "$depth" "$@"
