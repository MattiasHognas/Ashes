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

# Core lowering is one module split over `CoreLowering.ash` and its `CoreLowering.*.ash` parts. Writes all of
# their declarations as one text to the given file, without the parts' import lines and export lists, so the
# renderers extract a declaration by name or a range of declarations whichever part holds it. Each part starts
# with a `// Core lowering part: <file>` line. Prints the path.
core_lowering_source() {
    local target=$1
    local directory
    directory="$(cd "$(dirname "${BASH_SOURCE[0]}")/../packages/semantics/src/AshesCompiler/Semantics" && pwd)"
    awk '
        FNR == 1 { exporting = 0; part = FILENAME; sub(/^.*\//, "", part); print "// Core lowering part: " part }
        /^import / { next }
        /^export \($/ { exporting = 1; next }
        exporting { if ($0 == ")") exporting = 0; next }
        { print }
    ' "$directory/CoreLowering.ash" "$directory"/CoreLowering.*.ash > "$target"
    printf '%s\n' "$target"
}

# Prints the lines of a source from the first line matching the start pattern up to, not including, the first
# later line matching the end pattern. Fails when the start is missing, or when the range would run past the
# end of the part of the combined core lowering source that holds the start.
extract_range() {
    local source=$1 start=$2 end=$3
    awk -v start="$start" -v end="$end" '
        !copying && $0 ~ start { copying = 1; found = 1; print; next }
        copying && $0 ~ end { closed = 1; exit }
        copying && /^\/\/ Core lowering part: / { exit }
        copying { print }
        END {
            if (!found) { print "Missing range start: " start > "/dev/stderr"; exit 1 }
            if (!closed) { print "Missing range end: " end " after " start > "/dev/stderr"; exit 1 }
        }
    ' "$source"
}

# Frozen sources older than the frontend's `ExprNode(Int, Expr)` wrapper have no arm for it. Writes the source with
# an `ExprNode(_node, inner)` arm before every `ExprAt` arm, which looks through the node as the current
# production code does: the `ExprAt` arm's own right-hand side when it only recurses into `inner`, otherwise a
# call of the enclosing function with `inner` in place of its `Expr` parameter.
add_expr_node_arms() {
    awk '
        /^(let( recursive)?|and) [a-z][A-Za-z0-9_]* / {
            header = $0
            sub(/^(let( recursive)?|and) /, "", header)
            name = header
            sub(/[^A-Za-z0-9_].*$/, "", name)
            call = name
            rest = header
            while (match(rest, /\([a-z][A-Za-z0-9_]*: [^)]*(\([^)]*\))*[^)]*\)/)) {
                parameter = substr(rest, RSTART + 1, RLENGTH - 2)
                rest = substr(rest, RSTART + RLENGTH)
                parameterName = parameter
                sub(/:.*$/, "", parameterName)
                parameterType = parameter
                sub(/^[^:]*: /, "", parameterType)
                call = call "(" (parameterType == "Expr" ? "inner" : parameterName) ")"
            }
        }
        /^ *\| ExprAt\(/ {
            indent = $0
            sub(/\|.*$/, "", indent)
            arm = $0
            if (arm ~ /\| ExprAt\(_span, inner\) -> ./) {
                rhs = arm
                sub(/^.*\| ExprAt\(_span, inner\) -> /, "", rhs)
            } else {
                rhs = call
            }
            print indent "| ExprNode(_node, inner) -> " rhs
        }
        { print }
    ' "$1"
}
