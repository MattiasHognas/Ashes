#!/bin/bash
# usage: irfn.sh <program.ash> <binding> [which] [grep-pattern] ; stage 0's lowered IR of one binding of a program
# (the which-th function lowered from it, default 1), locations stripped. With a pattern, only matching lines,
# numbered by their position in the function.
. "$(dirname "$0")/env.sh"
src=$(readlink -f "$1"); name=$2; which=${3:-1}; pat=$4
E=$J/irfn
mkdir -p "$E"
cd "$R" || exit 1
dotnet run -c Release --no-build --project src/Ashes.Cli -- compile "$src" -o "$E/bin" --emit-ir lowered > /dev/null 2> "$E/raw.ir"
sed -E 's/ *\([^()]*:[0-9]+:[0-9]+\)$//; s/ +$//' "$E/raw.ir" \
    | awk -v n="from $name]" -v w="$which" '/^function / { on = (index($0, n) > 0); if (on) seen++ } on && seen == w' > "$E/fn.ir"
echo "lines=$(wc -l < "$E/fn.ir") ($(head -1 "$E/fn.ir" | cut -c1-80))"
if [ -n "$pat" ]; then grep -n -E "$pat" "$E/fn.ir" | cut -c1-150; else cut -c1-150 "$E/fn.ir"; fi
