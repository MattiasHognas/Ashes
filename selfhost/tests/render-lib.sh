#!/usr/bin/env bash
# Shared text operations for the focused fixture renderers. Source from Bash.
set -euo pipefail

extract_declaration() {
    local source=$1 name=$2
    awk -v wanted="$name" '
        /^(let( recursive)?|type( alias)?|export) / {
            if (printing) exit
            declaration = $0
            sub(/^(let( recursive)?|type( alias)?) /, "", declaration)
            sub(/[^[:alnum:]_].*$/, "", declaration)
            if (declaration == wanted) { printing = 1; found = 1 }
        }
        printing { print }
        END {
            if (!found) {
                print "Missing declaration: " wanted > "/dev/stderr"
                exit 1
            }
        }
    ' "$source"
}

rename_identifiers() {
    local names=$1 source=${2:--}
    awk '
        FILENAME == ARGV[1] { replacements[$1] = $2; next }
        {
            remaining = $0
            output = ""
            while (match(remaining, /[[:alnum:]_]+/)) {
                word = substr(remaining, RSTART, RLENGTH)
                output = output substr(remaining, 1, RSTART - 1)
                output = output (word in replacements ? replacements[word] : word)
                remaining = substr(remaining, RSTART + RLENGTH)
            }
            print output remaining
        }
    ' "$names" "$source"
}

prefix_module_exports() {
    local source=$1 type_prefix=$2 value_prefix=$3
    rename_identifiers <(awk -v types="$type_prefix" -v values="$value_prefix" '
        /^type / {
            name = ($2 == "alias" ? $3 : $2)
            sub(/[^[:alnum:]_].*$/, "", name)
            replacements[name] = types name
        }
        /^    \| [A-Z]/ {
            name = $2
            sub(/[^[:alnum:]_].*$/, "", name)
            replacements[name] = types name
        }
        /^    value / {
            name = $2
            sub(/[^[:alnum:]_].*$/, "", name)
            replacements[name] = values name
        }
        END { for (name in replacements) print name, replacements[name] }
    ' "$source") "$source"
}
