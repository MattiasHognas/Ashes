#!/usr/bin/env bash
# Render one project for the semantics, backend and IR parity suites of CHECKOUT.
# Print its fresh project directory; run the resulting binary from a fixture-enabled cwd.
set -euo pipefail
if (( $# > 1 )); then
    echo "usage: $0 [checkout]" >&2
    exit 2
fi
repo=$(realpath "${1:-$(dirname "$0")/../..}")
suites="$repo/selfhost/tests"
mkdir -p "$repo/work"
project=$(mktemp -d "$repo/work/shared-native.XXXXXXXX")
trap 'rm -rf -- "$project"' EXIT
echo '*' > "$project/.gitignore"
# Use the backend project's lock and overrides: its dependency graph includes both
# compiler packages used by the semantics and parity suites.
cp "$suites/backend/ashes.lock" "$project/"
sed -e 's/"ashes-compiler-backend-tests"/"ashes-compiler-shared-native-tests"/' \
    -e 's#../../packages/#../../selfhost/packages/#g' \
    "$suites/backend/ashes.json" > "$project/ashes.json"

for source in "$suites/semantics/"*.ash "$suites/backend/"*.ash; do
    module=${source##*/}
    [[ $module != Main.ash ]] || continue
    if [[ -e "$project/$module" || $module == SemanticSuite.ash || $module == BackendSuite.ash || $module == ParitySuite.ash ]]; then
        echo "duplicate generated module: $module" >&2
        exit 1
    fi
    cp "$source" "$project/"
done

# Extract only the top-level suite pipeline. The original functions and all-suite
# path are copied unchanged. Individual targets take Unit, including shipped partials.
for suite in semantics backend; do
    if [[ $suite == semantics ]]; then module=SemanticSuite; else module=BackendSuite; fi
    awk -v suite="$suite" -v cases="$project/$suite-cases.txt" '
        function fail(message) { print message > "/dev/stderr"; failed = 1; exit 1 }
        /^let run unit =$/ && suite == "semantics" { print "export (value run, value runNamed,)"; pipeline = 1 }
        !exported && /^(let |type |external )/ && suite == "backend" {
            print "export (value runAll, value runNamed,)"; exported = 1
        }
        /^let run shipped =$/ && suite == "backend" { pipeline = 1 }
        /^run\(Unit\)$/ && suite == "semantics" { stopped = 1 }
        /^match Ashes.IO.args with$/ && suite == "backend" { stopped = 1 }
        stopped { next }
        pipeline && /^    \|> / && !/^    \|> \(given \(_\) -> Ashes\.IO\.print\(/ {
            target = $0; sub(/^    \|> /, "", target)
            if (suite == "semantics") {
                sub(/^\(given \(_\) -> /, "", target); sub(/\(Unit\)\)$/, "", target)
                if (target !~ /^[A-Za-z0-9_]+\.run[A-Za-z0-9_]+$/) fail("unknown semantics pipeline entry: " $0)
                name = target; sub(/^.*\./, "", name)
            } else {
                if (target !~ /^[A-Za-z_][A-Za-z0-9_]*(\(shipped\))?$/) fail("unknown backend pipeline entry: " $0)
                name = target; sub(/\(.*/, "", name)
            }
            if (seen[name]++) fail("duplicate " suite " case: " name)
            targets[++n] = target; names[n] = name; print name > cases
        }
        { print }
        END {
            if (failed || !pipeline || !stopped || !n) exit 1
            if (suite == "backend") {
                print "let runAll root = run(loadShippedModules(root))"
                print "let runNamed root (name: Str) ="
                print "    let shipped = loadShippedModules(root)"
                print "    in match name with"
            } else {
                print "let runNamed (name: Str) ="
                print "    match name with"
            }
            for (i = 1; i <= n; i++) print "        | \"" names[i] "\" -> let _ = " targets[i] "(Unit) in Unit"
            print "        | _ -> Ashes.IO.panic(\"unknown " suite " test: \" + name)"
        }
    ' "$suites/$suite/Main.ash" > "$project/$module.ash"
done

# Capture mismatches as the original named renderer does. Preserve the comparison,
# diagnostic, fixture lists and final test.fail; thread only the output directory.
awk -v cases="$project/parity-cases.txt" '
    /^let fixturePath / { print "export (value run,)"; exported++ }
    /^let checkFixture root name =/ { sub("root name", "root output name") }
    /^let recursive checkFixtures root / { sub("root ", "root output ") }
    { gsub(/checkFixtures?\(root\)/, "&(output)") }
    /^                                    actual$/ {
        print "                                    let _ ="
        print "                                        match Ashes.IO.File.writeText(output + \"/\" + name + \".actual.ir\")(actual) with"
        print "                                            | Error(message) -> test.fail(message)"
        print "                                            | Ok(_) -> Unit"
        print "                                    in"; capture++
        print "                                        actual"
        if (getline <= 0 || $0 !~ /^                                    \|> mismatchReport\(name\)\(expected\)$/) exit 1
        print "    " $0
        if (getline <= 0 || $0 !~ /^                                    \|> Some$/) exit 1
        print "    " $0; next
    }
    /^let fixtures =$/ { list = 1 }
    list && /^    \]$/ { list = 0 }
    list && /^        "/ {
        name = $0; sub(/^        "/, "", name); sub(/",?$/, "", name)
        if (name !~ /^[[:alnum:]_]+$/ || seen[name]++) exit 1
        print name > cases; count++
    }
    /^match Ashes.IO.args with$/ { print "let run (root: Str) (output: Str) ="; entry++; next }
    /^    \| root :: \[\] ->$/ || /^    \| _ -> Ashes.IO.panic\("usage: ir-program-parity/ { next }
    { print }
    END { if (entry != 1 || exported != 1 || capture != 1 || !count) exit 1 }
' "$suites/ir-program-parity/Main.ash" > "$project/ParitySuite.ash"

cat > "$project/Main.ash" <<'ASH'
import SemanticSuite
import BackendSuite
import ParitySuite
match Ashes.IO.args with
    | "semantics" :: [] -> let _ = SemanticSuite.run(Unit) in Unit
    | "semantics" :: name :: [] -> let _ = SemanticSuite.runNamed(name) in Unit
    | "backend" :: root :: [] -> let _ = BackendSuite.runAll(root) in Unit
    | "backend" :: root :: name :: [] -> let _ = BackendSuite.runNamed(root)(name) in Unit
    | "parity" :: root :: output :: [] -> let _ = ParitySuite.run(root)(output) in Unit
    | _ -> Ashes.IO.panic("usage: shared-native semantics [case] | backend <shipped-library-root> [case] | parity <fixtures> <output-directory>")
ASH
mkdir "$project/actual"
trap - EXIT
printf '%s\n' "$project"
