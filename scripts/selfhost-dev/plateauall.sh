#!/bin/bash
# usage: plateauall.sh <small> <large> <template.ash>... ; plateau.sh for each template at two round counts, plain
# and with the reference-count poison on, one block per template. A leak shows as the large run's peak growing.
. "$(dirname "$0")/env.sh"
small=$1; large=$2; shift 2
for t in "$@"; do
    echo "== $(basename "$t")"
    bash "$HERE/plateau.sh" "$t" "$small" "$large"
    ASHES_RC_POISON=1 bash "$HERE/plateau.sh" "$t" "$large" | sed 's/^/poison /'
done
