#!/usr/bin/env bash
# Runs a command under gdb and reports where it crashed: the crash PC, the frames gdb can unwind
# that lie in the executable's code, and every stack word that points into the executable's code.
# Stage-1-linked executables carry no symbols, unwind tables or frame pointers, so the stack words
# are only candidate return addresses (a poor man's backtrace) and the list is approximate.
# With a link map (ASHES_LINK_MAP) every address is resolved to `symbol+offset`.
set -uo pipefail

usage() {
    cat <<'EOF'
Usage: bash selfhost/tests/behaviour/crash-trace.sh [--map <map>] [--words N] [--brief]
           [--keep-output] [--lines N] -- <command> [args...]

  --map <map>     link map of the executable; default <executable>.map when it exists
  --words N       stack words scanned from the stack pointer (default 4096)
  --brief         print one tab-separated line instead of the report:
                  signal, crash PC, key PC, key source
                  The key PC is the first address in the executable's code: the crash PC itself
                  (source `pc`), else the innermost gdb frame in the code (`frame`, e.g. under
                  abort), else the first stack word in the code that is not a symbol start
                  (`stack`). A run that does not crash prints `none`.
  --keep-output   leave the command's stdout and stderr on the terminal (default: discarded)
  --lines N       stack words listed in the report (default 80)

Core dumps are switched off for the run. Run it under a memory cap and a timeout; set the stack
limit the program needs (`ulimit -s 1048576` for stage-1 code) before calling.
EOF
}

script_dir=$(dirname "$(realpath "${BASH_SOURCE[0]}")")
map=""
words=4096
brief=0
keep_output=0
max_stack_lines=80
while [[ $# -gt 0 ]]; do
    case "$1" in
        --map) map=$2; shift 2 ;;
        --words) words=$2; shift 2 ;;
        --brief) brief=1; shift ;;
        --keep-output) keep_output=1; shift ;;
        --lines) max_stack_lines=$2; shift 2 ;;
        --) shift; break ;;
        -h|--help) usage; exit 0 ;;
        *) break ;;
    esac
done
if [[ $# -lt 1 ]]; then
    usage
    exit 2
fi

executable=$(command -v "$1" || true)
if [[ -z $executable || ! -r $executable ]]; then
    echo "crash-trace: cannot find executable $1" >&2
    exit 2
fi
if [[ -z $map && -r "$executable.map" ]]; then
    map="$executable.map"
fi

ulimit -c 0

# The executable's code: its first loadable, executable segment.
read -r text_start text_end < <(readelf -lW "$executable" 2>/dev/null | awk '
    $1 == "LOAD" && $0 ~ / R E / && !found {
        found = 1
        printf "%d %d\n", strtonum($3), strtonum($3) + strtonum($6)
    }')
if [[ -z ${text_start:-} ]]; then
    echo "crash-trace: $executable has no executable segment" >&2
    exit 2
fi

gdb_commands=(-batch -nx -q -ex 'set debuginfod enabled off' -ex 'set pagination off' -ex 'set confirm off' -ex 'set width 0')
if [[ $keep_output -eq 0 ]]; then
    gdb_commands+=(-ex 'set inferior-tty /dev/null')
fi
# In the report, gdb gets the map's functions as symbols too (link-map-symbols.sh), so its own
# backtrace and disassembly name them.
symbol_dir=""
if [[ $brief -eq 0 && -n $map ]] && command -v as > /dev/null && command -v ld > /dev/null; then
    symbol_dir=$(mktemp -d)
    trap 'rm -rf "$symbol_dir"' EXIT
    if bash "$script_dir/link-map-symbols.sh" "$map" "$symbol_dir/symbols.elf" > /dev/null; then
        gdb_commands+=(-ex "add-symbol-file $symbol_dir/symbols.elf")
    fi
fi
gdb_commands+=(
    -ex run
    -ex 'printf "CRASH-PC %#lx\n", $pc'
    -ex 'printf "CRASH-SP %#lx\n", $sp'
    -ex 'bt 32'
    -ex "x/${words}gx \$sp"
    -ex 'printf "STACK-RETRY\n"'
    -ex "x/${words}gx (((long) \$sp + 4096) & ~4095)"
)
gdb_output=$(gdb "${gdb_commands[@]}" --args "$@" 2>&1 < /dev/null)

# One line per finding: `signal NAME`, `pc ADDR`, `sp ADDR`, `frame ADDR`, `stack OFFSET ADDR`.
findings=$(printf '%s\n' "$gdb_output" | awk -v text_start="$text_start" -v text_end="$text_end" '
    function in_text(value) { return value >= text_start && value < text_end }
    /^Program received signal / {
        signal = $4
        sub(/,$/, "", signal)
        print "signal " signal
        next
    }
    /^Program terminated with signal / {
        signal = $5
        sub(/,$/, "", signal)
        print "signal " signal
        next
    }
    $1 == "CRASH-PC" { print "pc " $2; next }
    $1 == "CRASH-SP" { print "sp " $2; sp = strtonum($2); next }
    /^#[0-9]+ / {
        for (i = 2; i <= NF; i++) {
            if ($i ~ /^0x[0-9a-f]+$/) {
                if (in_text(strtonum($i))) print "frame " sprintf("0x%x", strtonum($i))
                break
            }
        }
        next
    }
    $1 == "STACK-RETRY" { retry = 1; next }
    /^0x[0-9a-f]+( <[^>]*>)?:/ {
        # The second dump starts at the next page; it is read only when the stack pointer itself
        # was unreadable (a stack overflow), so the two never overlap in the output.
        if (retry && dumped) next
        if ($0 ~ /Cannot access memory/) next
        if (!retry) dumped = 1
        address = strtonum(substr($1, 1, index($1, ":") - 1))
        first = 2
        if ($2 ~ /^</) first = 3
        for (i = first; i <= NF; i++) {
            value = strtonum($i)
            if (in_text(value)) {
                print "stack " sprintf("0x%x", address + (i - first) * 8 - sp) " " sprintf("0x%x", value)
            }
        }
    }
')

signal=$(printf '%s\n' "$findings" | awk '$1 == "signal" { print $2; exit }')
crash_pc=$(printf '%s\n' "$findings" | awk '$1 == "pc" { print $2; exit }')

resolve() {
    if [[ -n $map ]]; then
        bash "$script_dir/resolve-address.sh" "$map" "$@" | awk '{ $1 = ""; sub(/^ /, ""); print }'
    else
        printf '%s\n' "$@" | sed 's/.*/(no map)/'
    fi
}

if [[ $brief -eq 1 ]]; then
    if [[ -z $signal ]]; then
        printf 'none\t-\t-\t-\n'
        exit 0
    fi
    key=""
    source=""
    if [[ -n $crash_pc ]] && (( crash_pc >= text_start && crash_pc < text_end )); then
        key=$crash_pc
        source=pc
    fi
    if [[ -z $key ]]; then
        key=$(printf '%s\n' "$findings" | awk '$1 == "frame" { print $2; exit }')
        [[ -n $key ]] && source=frame
    fi
    if [[ -z $key ]]; then
        candidates=$(printf '%s\n' "$findings" | awk '$1 == "stack" { print $3 }')
        if [[ -n $candidates && -n $map ]]; then
            key=$(bash "$script_dir/resolve-address.sh" "$map" $candidates | awk '$2 != "?" && $2 !~ /\+0x0$/ { print $1; exit }')
        elif [[ -n $candidates ]]; then
            key=$(printf '%s\n' "$candidates" | head -n 1)
        fi
        [[ -n $key ]] && source=stack
    fi
    printf '%s\t%s\t%s\t%s\n' "$signal" "${crash_pc:--}" "${key:--}" "${source:--}"
    exit 0
fi

if [[ -z $signal ]]; then
    echo "no crash: the command ran to completion under gdb"
    printf '%s\n' "$gdb_output" | grep -E '^\[Inferior .* exited' || true
    exit 0
fi

echo "signal: $signal"
echo "map: ${map:-none (addresses unresolved; pass --map or put <executable>.map beside it)}"
if [[ -n $crash_pc ]]; then
    if (( crash_pc >= text_start && crash_pc < text_end )); then
        echo "crash pc: $crash_pc $(resolve "$crash_pc")"
    else
        echo "crash pc: $crash_pc (outside the executable's code; gdb: $(printf '%s\n' "$gdb_output" | awk '/^#0 / { $1 = ""; print; exit }'))"
    fi
fi
# Folds a run of lines that agree after their first field (a frame number or stack offset) into
# the first one, so deep recursion does not flood the report.
collapse_repeats='
function flush() {
    if (first != "") print "  " first (repeats > 0 ? "   (and " repeats " more like it right after)" : "")
}
{
    key = $0
    sub(/^[^ ]+ /, "", key)
}
seen && key == previous { repeats++; next }
{
    flush()
    first = $0
    previous = key
    repeats = 0
    seen = 1
}
END { flush() }
'
frames=$(printf '%s\n' "$findings" | awk '$1 == "frame" { print $2 }')
if [[ -n $frames ]]; then
    echo "gdb frames in the executable's code (the first is exact; later ones come from gdb's prologue"
    echo "analysis without unwind tables and may be wrong):"
    # shellcheck disable=SC2086
    paste -d ' ' <(printf '%s\n' "$frames") <(resolve $frames) | awk '{ print "#" NR - 1 " " $0 }' | awk "$collapse_repeats"
fi
echo "stack words pointing into the executable's code (APPROXIMATE backtrace: candidate return"
echo "addresses found by scanning ${words} words from sp; stale values and code pointers appear too,"
echo "and a symbol start (+0x0) is usually a closure code pointer, not a return address):"
stack_offsets=$(printf '%s\n' "$findings" | awk '$1 == "stack" { print $2 }')
stack_values=$(printf '%s\n' "$findings" | awk '$1 == "stack" { print $3 }')
if [[ -n $stack_values ]]; then
    # shellcheck disable=SC2086
    paste -d ' ' <(printf '%s\n' "$stack_offsets" | sed 's/^/sp+/') <(printf '%s\n' "$stack_values") <(resolve $stack_values) \
        | awk "$collapse_repeats" | awk -v limit="$max_stack_lines" 'NR <= limit { print } END { if (NR > limit) print "  ... " NR - limit " more lines (raise --lines)" }'
fi
