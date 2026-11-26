# Typed lowering measurement

Run from the repository root. The driver reads shipped modules from `lib/Ashes` and takes a project manifest (or a single `.ash` file), optionally the mode `typed` (the only one: the compiler lowers from the table, and the stitched-program lowering a `legacy` mode measured is gone), and optionally `--goal-details`. It loads and stitches the project, numbers its syntax, runs inference and lowers the identified program with its recorded node types.

Build with a current stage-0 compiler in the coordinator’s shared build slot. Set `S0` to its absolute path and `OUT` to a fresh absolute directory. The example measures the self-hosted CLI.

```bash
set -e
ulimit -s 1048576
ulimit -c 0
mkdir -p "$OUT"
systemd-run --user --scope -q -p MemoryMax=24G -p MemorySwapMax=0 timeout 5400 \
    "$S0" compile --project selfhost/tests/typed-lowering-measurement/ashes.json \
    --debug -O0 -o "$OUT/measure" >"$OUT/build.log" 2>&1
systemd-run --user --scope -q -p MemoryMax=12G -p MemorySwapMax=0 timeout 3600 \
    "$OUT/measure" selfhost/packages/cli/ashes.json typed >"$OUT/typed.tsv" 2>"$OUT/typed.err"
```

Progress markers on stderr bracket project loading and stitching, inference, and lowering. If discovery requests replay, the driver reports the transition before replay begins. Progress writes are excluded from the reported timing intervals. The final stderr summary gives the number of traversals (one discovery, optionally one retained discovery and one replay emission) and the body re-lowering counters for each. As in the TSV, each counter describes the lowering state kept by its traversal.

Add `--goal-details` after the mode to capture lexical evidence when a site is deferred. The report on stderr includes the stitched source region, the start and end offsets in the goal key, trait and method, numbered node ID where available, original and settled operand types, and the active trait-method bindings at that occurrence. Structural equality keys also appear on stderr without this option. Detailed collection adds work inside lowering, so use the same option for runs whose times are compared.
The details also name, as `deferred-operator goal` lines, every `+`, `-`, `*`, `==` and `!=` the first pass emitted in its speculative integer form because both operands were still open, whether or not a replay follows; a site with no span is keyed `unspanned:<trait>:<method>`. Their number equals `first_deferred_operators` minus the deferred ordered comparisons, which appear as trait goals. A body lowered again can name a site once per lowering. In `typed` mode each detail line ends with what the table recorded for the site's node: `recorded_operator=<trait>(<operand type>) <primitive|declared|dictionary>` and `recorded_type=`. `recorded_operator=none` means the site was lowered at a node lowering synthesized, under the identity of the node around it; an operand type that is a bare variable means a generic body lowered without an instantiation that closes it.
The traversal report also prints `concrete_copy_calls` counts for the first and emission passes on stderr. A recorded call passed the source-copy proof using its node facts. An inferred call entered the existing speculative inference path. The counts describe the state retained by each pass, including cache hits, and exclude discarded attempts and the optional retained-only traversal.

A successful run exits zero and prints a TSV header and one data row. A project, parse, inference or lowering error terminates the run with a diagnostic. A lowering failure can still produce a partial counter row, so check the exit status before using the data. Keep the stage-0 and driver source revisions with the output.

| Columns | Meaning |
| --- | --- |
| `mode`, `stitch_ms`, `infer_ms` | Selected lowering mode, time to load/parse/stitch, and time to identify and infer. |
| `first_ms`, `replay_ms`, `replayed` | Measured first traversal and replay durations and whether replay ran. The TSV keeps its Boolean replay indicator. Replay time includes any retained-program traversal required before emission. |
| `pending_traits`, `equality_goals` | Trait goals and structural equality goals left by the first traversal. |
| `first_*`, `replay_*` | For each observed pass, operators answered from recorded types, deferred operators, function bodies lowered again, calls using recorded expected results, and calls retried after that recorded expectation failed. Absent replay has zero counters. |
| `pending_keys` | Space-separated pending trait site keys. Empty when there are no keys. |

The typed counter gate requires zero pending traits, zero equality goals and `replayed=no`. Record the body re-lowering count separately. Measurements use the Core lowering API with reuse enabled and a manifest-level source context. The production CLI, backend, stage-2/3 comparison and behaviour suites provide separate acceptance evidence.

## Test-program sweep

The driver also takes a single `.ash` file in place of a manifest; it is stitched with the shipped modules it imports, as `compile <file>` does. `sweep.sh <measure> <typed|legacy> <out>` runs it over every `tests/*.ash` that is not an `expect-compile-error` fixture, one program at a time under a 4 GiB cap, and writes `rows.tsv` (the program name, then the 19 columns above), `failed.txt` (programs whose run did not exit zero with a complete row: stitch or lowering errors), `rejected.txt` (programs the inference rejects, such as the `_fails` diagnostics fixtures; `__TCP_PORT__` is replaced by a number first, as the test runner substitutes a port) and `summary.txt` (programs, replays, pending trait goals, structural equality goals, first-pass deferred operators). The goal details of every program that replayed or deferred an operator are kept in `<out>/<name>.err`. Run it from the repository root with the same driver binary for both modes.

## Step-8 shadow comparison and IR hash

The driver always reports, on stderr, one `shadow rule=<n> agree=<a> differ=<d> unrecorded=<u>` line for each of the eleven order-dependent rules of the step-8 plan (`docs/md/future/SELF_HOSTING.md`, step 8): at each site where the rule decides, the decision it took is compared with the same decision taken on the final type the table records for the site. `agree` counts the sites where both decide alike, `differ` the sites where they do not, and `unrecorded` the sites the table answers nothing for (a node lowering synthesized, or a recorded type the body's instantiation and the settled variables leave open). The comparison changes nothing lowering emits; it runs only when the lowering observes its types, so the compiler never computes it. Sites are counted in the lowering each pass kept; a body lowered again counts its sites once, in the lowering that stays.

| Rule | Site | Current answer | Final-type answer |
| --- | --- | --- | --- |
| 1 | an operator over two open operands emitted in its integer form | `Int` | the operand type recorded for the operator |
| 2 | a trait goal or dictionary left for a later pass, or answered by an earlier pass's proof | `pending`, or the proven type | the operator's recorded operand type or the reference's recorded dictionary of that trait |
| 3 | `==`/`!=` over two open operands, or a trait method through a generic body's hidden evidence | `evidence` or `no-evidence` | `evidence` when the table records the operator's instance as a dictionary |
| 4 | a body lowered again because its first lowering closed a scope type | `relower` | `once` |
| 5 | an internal deep copy | `pass-through` or `copy <type>` | `copy <recorded type>` |
| 6, 7, 8 | the arena-result request of a call, filed under a deferred self result (6), a sibling's open or pinned result (7) or the result layout (8) | `arena` or `no-arena` | the request judged on the call's recorded result type |
| 9 | a known callee asked for an arena result because its body was not lowered yet | `arena` | `no-arena` when the callee's result fact is known once every body is lowered, otherwise agreeing |
| 10 | a body lowered again because a call result resolved | `relower` | `once` |
| 11 | entry normalization of a parameter that reaches the result | `normalize` or `keep` | the same decision on the parameter type recorded for the lambda |

`--ir-hash` hashes each IR line with the entry file's path, as given, replaced by its base name, so two sweeps compare whatever their out directories are and whether the path was spelled absolute or relative; the shipped modules are always read from `lib/Ashes`, relative to the repository root the driver runs from.

`--shadow-details` also prints every site that does not agree as `shadow-site<TAB>rule<TAB>source:start-end:node=<id><TAB>current<TAB>final`, where an unrecorded site reads `unrecorded: <reason>`: no node in effect, a synthesized node, no type recorded at the node, a type left open or untranslatable, a trait-method call (rule 3: the table records operators, not trait-method calls), a node that binds no parameter of that name (rule 11: an applied or synthesized lambda is lowered under its call's node), or a node whose recorded type is not the call's closed result (rules 6 to 8), or, for rules 6 to 8, a result type closed only by the variables the program's uses settle and not by the body's instantiation (`closed only by settled variables, <answer>`: the request the settled type would make).

The node each rule reads: the operator's node for rules 1 to 3 (operands are lowered under their own nodes and the operator's is restored before it is emitted); a dictionary goal of a constrained call reads the call's root reference, where the inference records the instantiation; the outermost call of a spine for rules 6 to 8 (the request is emitted only for the last application, and a spine's inner calls are flattened without entering their nodes); the deep-copy call for rule 5; the lambda's node for rule 11, checked by the parameter binding the inference notes there. Rule 9 is exact: the callee result facts and returned-closure labels it reads are accumulated over the whole program and carried back from every body, so the finished lowering's state holds every body's. `--ir-hash` lowers the program again exactly as the compile command does (`lowerCoreNumberedProgramWithRegionsAndReuse` with reuse on) and prints `ir_hash=<hash> ir_lines=<lines>` for its lowered IR text, so two sweeps compare program by program with one diff of their `ir.tsv`.

`sweep.sh` passes all three options and writes, beside `rows.tsv`: `shadow.tsv` (program, rule, agree, differ, unrecorded), `shadow-sites.tsv` (program, rule, site, current, final) and `ir.tsv` (program, hash line); `summary.txt` gains one total line per rule with the number of programs that have a differing site.

`--single-lowering` measures what re-lowering costs: the lowering keeps the first lowering of every body that asks to be lowered again (step-8 rules 4 and 10) instead of lowering it a second time. Its `first_ms` set against an ordinary run's on the same program is the time re-lowering takes; the program it lowers is not meant to be used, and a lowering error it stops at is printed after the row. The flag changes nothing outside the measurement: the lowering reads it from its type observation, which only the driver sets up.

Shadow sites name their file through the stitched module regions, which the driver passes to the lowering under `--goal-details` or `--shadow-details`.
