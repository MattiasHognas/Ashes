#!/bin/bash
# usage: mktinyinputs.sh <count>... ; writes $J/tiny/loops<count>.ash for each count: that many copies of one
# recursive list function with a match, a record and a let, plus a main that calls them all. Two counts give the
# census a per-function difference in the shape the self-hosted sources are mostly made of.
. "$(dirname "$0")/env.sh"
for count in "$@"; do
    out=$J/tiny/loops$count.ash
    {
        printf 'type Item =\n    | name: Str\n    | weight: Int\n\n'
        for i in $(seq 1 "$count"); do
            printf 'let recursive walk%s (items: List(Item)) (seen: List(Str)) (total: Int) =\n' "$i"
            printf '    match items with\n'
            printf '        | [] -> total + Ashes.Collection.List.length(seen)\n'
            printf '        | item :: rest ->\n'
            printf '            let next =\n'
            printf '                if item.weight > %s\n' "$i"
            printf '                then item.name :: seen\n'
            printf '                else seen\n'
            printf '            in walk%s(rest)(next)(total + item.weight)\n\n' "$i"
        done
        printf 'let items = [Item(name = "a", weight = 1), Item(name = "b", weight = 40)]\n\n'
        printf 'Ashes.IO.print(0'
        for i in $(seq 1 "$count"); do printf ' + walk%s(items)([])(0)' "$i"; done
        printf ')\n'
    } > "$out"
    echo "wrote $out"
done
