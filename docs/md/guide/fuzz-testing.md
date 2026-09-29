# Fuzz testing

`Ashes.Fuzzing` is a standalone property-based compiler-testing application. It is separate from
`Ashes.Tests` so campaigns can own their case counts, seeds, process timeouts, native executables,
shrinking, and artifacts without turning the ordinary unit-test runner into a campaign scheduler.
`Ashes.Fuzzing.Tests` contains only fast deterministic tests of the framework itself.

The generator asks a registry of expression rules for an expression of a required immutable
generation type. The initial catalog includes generic tree and Maybe-like ADTs in addition to
scalars, tuples, lists, functions, records, Results, and Tasks. Record construction resolves field
names and types from the current declaration context instead of a fixed record shape. ADT generation
likewise resolves constructors and substitutes generic field types from the declared schema. Its
immutable context tracks typed lexical values and functions, ADTs, records,
typed capability operations, active handlers, suspension/resource/recursion/tail-position flags,
ownership-interest tags, active templates, and inherited feature state. Profiles restrict these
flags and interests before generation, and effect, recursion, resource, and targeted reuse templates
enforce them as preconditions. Its explicit node,
depth, collection, recursion, source-size, and combination budgets guarantee termination. A second
validation pass measures complete-program declarations, functions, ADTs, maximum match-arm count,
maximum collection length, recursion complexity, and every expression root in addition to those
recursive generation limits. A case that exceeds any configured dimension falls back to a bounded
typed leaf or fails generation when even the minimum complete program cannot fit. A second registry
contains generic combination templates for sharing, cross-branch aliases, escaping and
shared-capture closures, guarded and ADT matches, list and constructor reconstruction, bounded
recursion, nested capability handlers, values and closures captured across `await`, matches before
and after suspension, deterministic task spawning, reuse of completed task results, Result
constructors, `let?` binding, success propagation, error mapping, bounded recursive functions
returning `Result`, and Perceus reuse and fallback shapes. Targeted Perceus templates distinguish
unique record updates, shared
reconstruction fallback, reconstruction while the candidate remains captured by a live closure,
branch-selective reconstruction, nested reusable constructors, aliased results that prevent reuse,
fresh results that permit reuse, shared aliases kept live across tail recursion, and list-tail
traversal through a capturing tail-recursive function. Coverage metadata separately records results
that alias inputs,
fresh aggregate results with internal sharing, runtime uniqueness checks, and statically unique
constructor-update paths. Templates may fill their holes with the ordinary generator, so
difficult feature interactions arise compositionally. Capability templates also exercise
Result-returning operations and capability-provided values at the base case of bounded recursive
list traversal. The initial interaction catalog also
guarantees captured-ADT matching inside closures, closures
inside match branches, recursive list reconstruction, Result pipelines whose continuation is a
generated closure, and handled capability operations performed from a closure selected by a match.
It also guarantees nested matching over a generic tree and a bounded tail-recursive loop carrying
and reconstructing a tree accumulator. These templates remain generic over their payload or result
types.

The in-process IR oracle verifies function, block, local-slot, string-literal, and external-call
references; external signature arity; ADT and reuse layout operands; definite temp definitions;
reuse-token production and consumption; and path-aware ownership consumption so a temp used after
`RcDrop`, resource cleanup, or `DropReuse` fails the case before backend execution.

Every complete generated program also places the result behind
an explicit type annotation, forcing inference to prove the requested generation type. Complete
program generation emits deterministic top-level functions and values, generic ADTs, records,
mutual-recursion groups, and static capability providers in addition to the trailing generated
expression, so parser, inference, lowering, and ownership checks exercise declaration stitching and
sequential top-level scope rather than only isolated expressions. The formatter oracle compares the
original `AST -> format` source with `parse -> format`, then reparses and formats again to require
byte-identical idempotence across both round trips.

## Running campaigns

Run the bounded standard suite with:

```sh
just fuzz
```

The pre-commit-sized fixed-seed pass is part of `just ci-quick`. A configurable manual campaign
for a large Perceus run is:

```sh
just fuzz-long -- --profile perceus --cases 100000 --seed 12345 --seeds 4 --max-nodes 120 --campaign-timeout 3600
```

Run the native memory-growth profile with:

```sh
just fuzz-memory -- --cases 10 --max-nodes 60 --campaign-timeout 3600
```

For every generated observable program, this profile builds three repeatable workloads containing
2,000, 10,000, and 50,000 evaluations. Each evaluation fully renders the generated value and folds
its byte length into an integer checksum, forcing the value graph to be traversed and then dropped.
The oracle requires the checksum per iteration to remain identical at every scale, measures native
peak RSS through `/usr/bin/time` on Linux and peak working set through `Process.PeakWorkingSet64` on
Windows, rejects a peak of 64,000 KB or more, and rejects either total or
late growth of 8,192 KB or more. This distinguishes a fixed allocator high-water mark from retained
per-iteration values. It runs only when the selected target executes natively on a Linux or Windows
host and defaults to three cases. Concurrent task and suspension shapes are excluded because
outstanding background work is still live memory rather than a dropped-value leak; dedicated async
RSS tests cover those lifecycles. Console-writing shapes are excluded too: one line per evaluation
exceeds the output limit at 50,000 iterations and says nothing about retained memory. The profile is kept out of `just ci-quick` and `just fuzz` because
each case performs three native compilations and executions, but one case runs in every 50-case
rotation of the `all` profile.

The `leak-census` command checks for leaks exactly rather than by memory growth, and runs every
case once:

```sh
dotnet run --project src/Ashes.Fuzzing -- leak-census --seeds 3 --templates scripts/selfhost-dev/reproducers
```

It forces each combination template of the profile (`memory-growth` by default) once per seed, adds
every `.ash` file of the `--templates` directory that names its repeat count `ROUNDS`, builds each
case with `ASHES_RC_LEAKCHECK=1` at one round of work and at two, and reports every case whose
second round leaves reference-counted cells live that the first did not, with the number of cells a
round leaks. The program reports its live cells at its end, and a value it keeps to its end is live
in both runs, so the difference is exactly what one round leaked: a single leaked cell shows. A leaking
case's source and a `report.tsv` of every case go to `<artifacts>/leak-census/`; the command exits 1
when any case leaks or yields no count. `--only <id>[,<id>...]` keeps only the named combination
templates, so one class is measured in seconds, under a placement switch (`GRC_NO_...=1`) to show what
its rule fixes.

With `--stage1 <path>` naming a self-hosted compiler (`bash scripts/selfhost-dev/quickstage1.sh
<tag>` builds one at `artifacts/selfhost-dev/s1-<tag>`), every case is also compiled by that
compiler at one round and at two, under a 1 GB stack and with the same leak check, which stage 1's
backend keeps in its region allocator. Each case then reports one cell per compiler, `ok`, `LEAK n`
or `SKIP reason`, and `report.tsv` carries stage 1's four columns after stage 0's; a case leaking
under either compiler fails the run. A class that leaks under stage 1 but not stage 0 is a stage-1
placement rule still to be ported; the reverse is a stage-0 rule stage 1 does not need.

With `--poison`, every case is also built once by stage 0 at two rounds with `ASHES_RC_POISON=1`,
which overwrites every freed cell, and run: a program that ends with a signal, a non-zero exit or a
hang read freed memory, and the case reports `UAF` with the exit after its leak cells, fails the run
like a leak, and has its source written beside the report. Two templates carry the use-after-free
shapes the census knows: a generic walk whose exit arm returns a string bound under a nested pattern
(`| last :: [] -> last` and three shapes like it) while the `[] -> ""` arm fixes the type only later,
and a loop over a list shared by every round whose record literal successor holds a span matched out
of that list. Neither leaks; both fail under `--poison` when their rule is switched off
(`GRC_NO_DEFERREDPATTERNOWNER`, `GRC_NO_BORROWEDCHILDKEEP`).

The `leak.*` templates give the census the shapes that leak. Each generates a parametric program of
one class of reference-count leak found in the self-hosted compiler: a record whose list field grows
in the record-update successor of a loop, a map threaded through `upsertStr` while a list is walked,
a loop over a recursive ADT whose fallback arm returns its parameter whole, a helper returning a
tuple with a `None` element, substitution walks returning a list and a supply record (through a
nested `let recursive` helper or through pair-returning helpers), an exit arm whose record holds the
loop's parameter, a list accumulator matched out of a helper's record result, a variant accumulator
rebuilt by a helper, a worklist solver, an unannotated generic pruning helper returning a pair, a
let-bound tuple destructured into a result tuple, an optimizer pass matching a helper's pair of state
and rewritten record, a list parameter returned inside a tuple, a nested group record replaced by a
helper, a lowering-state pipeline, two walks that leak one cell a round, an unannotated loop's state
in its exit tuple, an item loop whose pending list blocks its state, a fresh list built in the
program's trailing pipe, a worklist whose front queue is walked and refilled from its back queue, a
record consed onto a producer's spine by a let, by a closure over it, or under a let-bound self call,
three classes still open (a list accumulator replaced by a helper's rebuild in one arm and handed on
in the other, a pairwise concatenation consed onto an accumulator, a record built through two piped
closures and consed onto the recursive tail), and the two use-after-free shapes above. Names, list lengths, element values and types, updated fields,
and the variant of each class vary with the seed, and every choice is recorded in the case's trace.

Every leak or use-after-free fixed by a lowering rule since the census was introduced has a template
that shows it. Measured with the rule's switch set (`--only <template> --poison` under `GRC_NO_...=1`),
each class leaks or fails on the three default seeds, or leaks more where the class is still open,
and the closed classes are clean with the rule on. A fix that only stage 1 needed is covered by the
stage-1 column of the template named, with the growth its commit measured, and a fix the census cannot
see says why.

| Fix | Template | With the rule off |
|---|---|---|
| A comparison borrows both operands | `leak.fixpoint-compares-rebuilt-list` | `GRC_NO_BORROWEDCOMPARE`: 42, 30 and 42 cells per round |
| A list of scalars handed on in one arm and replaced by a helper's result in another lives on the reference-counted heap | `leak.accumulator-replaced-by-helper-in-one-arm` | `GRC_NO_HANDEDONFRESHLIST`: 24, 24 and 17 cells per round |
| A spine cell's head retained out of a let binding leaves the binding its own reference | `leak.spine-head-bound-by-let` | `GRC_NO_TMCHEADRETAIN`: 5, 5 and 2 cells per round |
| A reference-counted cell takes a reference to a recursive producer's let-bound result, or a copy of an arena one | `leak.self-call-tail-under-owned-head` | `GRC_NO_LETCALLTAIL`: segfault on every seed poisoned (`UAF`, exit 139), plain on one (the other two leak 1 cell per round) |
| A parameter reaching the result only as a list cell's head is not normalized at entry | `leak.closure-conses-its-parameter` | `GRC_NO_CELLHEADPARAM`: 3, 4 and 5 cells per round |
| 59fa6dd1 A loop releases an earlier iteration's result when no successor names it or a part of it | `leak.worklist-solve-records` (still open: 6, 14 and 22 cells per round with every rule on) | `GRC_NO_PREDECESSORNAMES`: 46, 75 and 154 cells per round |
| dd18b233 A retain of an unannotated loop parameter in an exit tuple is withdrawn behind the copy-out | `leak.unannotated-loop-state-in-exit-tuple` | `GRC_NO_DEFERREDCOPYREAD`: 5, 4 and 7 cells per round |
| b75fc5ca A reference handed over for an arena result to keep goes to an owned slot | `leak.variant-accumulator-through-helper`, `leak.list-parameter-in-tuple-result` | `GRC_NO_HANDEDSLOT`: 5, 10 and 18, and 8, 4 and 4 cells per round (`leak.header-tuple-destructured`, still open at 1, grows to 1, 10 and 11) |
| d7e17793 A generic type's fields are read at its instantiation when a call's result may hold an argument | `leak.lowering-state-pipeline` | `GRC_NO_FIELDINSTANCE` alone leaves it clean, since b75fc5ca's owned slot now releases the reference this rule withdraws; with `GRC_NO_FIELDINSTANCE` and `GRC_NO_HANDEDSLOT` set, 2, 3 and 2 cells per round (the class reproducers c_var_min1 and c_v2: 6) |
| 05071e4c An arena loop parameter takes an arena copy of a successor retained for it | `leak.nested-let-recursive-helper`, `leak.helper-returns-list-and-state` | `GRC_NO_ARENASUCCESSORCOPY`: 4, 5 and 5 cells per round; 2 and 4 on two seeds |
| 4ab593cd An arena tuple beside a generic value copies its reference-counted elements | `leak.generic-pair-pruning`, `leak.helper-returns-list-and-state` | `GRC_NO_DEMOTE`: 4 cells per round on every seed; 3, 2 and 4 |
| 47699bd5 A loop releases an earlier iteration's result when its successors are parts of the newer one | `leak.accumulator-from-call-result-part` | `GRC_NO_OWNEDSLOTPART`: 24, 11 and 23 cells per round |
| 19a20874 Pair each construction retain with the release that balances it (the leak half) | `leak.record-update-successor`, `leak.record-update-single-cell`, `leak.exit-arm-record-holds-loop-parameter` | `GRC_NO_COPIEDCELLRETAINS`: 5, 11 and 6; 3, 1 and 3; 2 cells per round on every seed (`GRC_NO_KEEP` alone changes nothing) |
| 9adf95b1 The map's reuse specialization releases what it copies and borrows its arguments | `leak.map-threaded-while-walking` | 90, 30 and 54 cells per round on the three seeds before the fix (no switch; from the fix's census) |
| 2ba42f15 A handler loop keeps a literal string match arm in the arena | `capability.recursive-list`, `capability.deterministic-handler` | 1 cell per round on three seeds and on one (no switch; from the fix's census) |
| 84de7a26 A loop exit returning a pattern binding of its parameter hands its retain to the result | `leak.loop-parameter-returned-whole` | 2, 4 and 2 cells per round (no switch; from the fix's census) |
| bb5c50c8 A helper pairing its state parameter with a rewrite releases the parameter after the pair | `leak.pass-state-and-rewrite-pair` | 4, 3 and 4 cells per round (no switch; from the fix's census) |
| 622a1246 A record update of a loop parameter handed to a call releases its retains after the call | `leak.nested-group-replaced-by-helper` | 22, 10 and 18 cells per round (no switch; from the fix's census) |
| 7e978033 The program entry releases its owned call results at its end | `leak.entry-trailing-pipe-fresh-argument` | `GRC_NO_ENTRYRELEASE`: 4, 3 and 4 cells per round |
| 20e52bdf A worklist's front queue, walked and refilled, lives on the reference-counted heap | `leak.worklist-front-walked-and-refilled` | `GRC_NO_WALKREFILL`: 24 cells per round on every seed |
| 781beb18 A loop exit returning a head bound under a nested pattern hands the result a reference of its own | `leak.loop-exit-head-under-nested-pattern` | `GRC_NO_DEFERREDPATTERNOWNER` with `--poison`: UAF (exit 1) on every seed |
| 19a20874 Pair each construction retain with the release that balances it (the use-after-free half) | `leak.borrowed-child-of-literal-successor` | `GRC_NO_BORROWEDCHILDKEEP`: segfault on every seed, plain and poisoned |
| b28f6459 A program that declares capabilities keeps the ownership contract unless a handler can leave a post pending | none | Not measurable by the census: with the contract off the program makes no reference-counted cells (classes 3, 13, 15 and 17 moved whole into the arena), so the count cannot grow; peak memory is the only measure, and `tests/capability_provided_without_handler_keeps_ownership_contract.ash` pins the lowering |
| edef0a98 Stage 1 releases an argument handed to a call whose result is a contract type | `leak.pass-state-and-rewrite-pair`, stage-1 column | stage 1 grew 69 MB to 1257 MB between 1,000 and 20,000 rounds on the class reproducers before the fix (no switch; stage 1 has none) |
| 6aa23e6e Stage 1 counts a loop's record parameter as placed only when no sibling blocks it | `leak.nested-item-loop-pending-blocks-state`, stage-1 column | 26 MB to 63 MB between 100,000 and 300,000 rounds before the fix; the class still leaks 9 to 14 cells per round under both compilers |
| ad9238ca Stage 1 releases an earlier iteration's result when its successors are parts of the newer one | `leak.worklist-front-walked-and-refilled`, stage-1 column | 9.7 MB to 237 MB between 1,000 and 100,000 rounds before the fix |
| 956aa0c9 Stage 1 owns a self call's result once its type turns out to be a contract type | `leak.worklist-front-walked-and-refilled`, stage-1 column (the tree insert inside `Node`) | 7.7 MB to 52 MB over 100,000 rounds before the fix |
| d487a7b5 Stage 1's backend starts every local slot at zero | `leak.map-threaded-while-walking`, stage-1 column | a map built by `keysOf(1)` and folded hung or segfaulted at one round, which the census reports as `SKIP program timed out` or `SKIP program exit 139` |
| 9412ab2c Stage 1 hands a constructor's fresh child to an owned slot | `leak.record-update-successor`, stage-1 column (a string field computed by a call) | 30 MB to 2.27 GB between 1,000 and 100,000 rounds before the fix |
| 5a785cdc Stage 1 lowers a let-bound self-call as an ordinary call, not a back edge | none | A wrong result, not a leak: the census does not compare output; `tests/let_bound_self_call_before_tail_self_call.ash` pins it |
| bf215f6e A back-edge helper is spliced only when every helper it calls is spliced too | none | A false ASH014, not a leak: a template with the shape would report `SKIP compile exit 1`; `tests/back_edge_helper_calling_unreachable_helper_is_called.ash` pins it |

A leak template's program needs types and top-level functions that an expression cannot declare,
so it is an `IDeclaringCombinationTemplate`: a case that forces it adds its declarations, which end
in an entry function `Int -> Int`, and the template's expression applies that function, so the work
runs each time the census evaluates the case's value. A class that leaks only in the program's entry
(a fresh argument of the trailing pipe, `0 |> pass(build(N)([]))`, which a function releases at its
return but the entry must release at its end) cannot show inside that function, so a template may
also give the census a trailing workload: an expression naming its rounds count `ROUNDS`, which the
census runs as the program's trailing expression after the declarations, here with the rounds count
as the built list's length. `leak.entry-trailing-pipe-fresh-argument` is that template; the other
profiles still call its entry function. The declarations widen the case's budget by
their own size, the case records the node budget it was requested with for replay, and its prelude
holds only the stable generation types, because a case-varying declaration such as a capability
provider changes whether several of these classes leak. Declaring templates come last in a
profile's rotation, so adding one leaves the case that forces every other template unchanged. They
run in the native observable profiles (`memory-growth`, `compile`, `differential`, `cross-target`)
and in `all`, not in the in-process profiles, whose cases keep the whole program within their node
limit.

List profiles, rules, combinations, and oracles with:

```sh
dotnet run --project src/Ashes.Fuzzing -- list
```

Profiles include `syntax`, `semantics`, `perceus`, `combinations`, `compile`, `differential`,
`memory-growth`, `invalid-source`, `invalid-semantics`, `traits`, `traits-differential`, `async`, `capabilities`,
`pattern-language`, `resources`, `cross-target`, and `all`. The `pattern-language` profile combines
named record, as-, and or-patterns and runs parser, formatter, semantic, and IR verification oracles.
The `traits` profile generates coherent user trait
declarations, concrete and conditional implementations, constrained generic functions,
multi-parameter traits, derived implementations, concrete resolution sites, and generic
trait/closure and derived-operator/sharing combinations. Its trace records every generated
declaration, implementation, constraint, and selected resolution. The `invalid-semantics` profile
keeps syntax parseable while rotating overlap, orphan, ambiguity, missing-implementation,
implementation-shape, and supertrait-cycle failures with an exact expected diagnostic code.
`traits-differential` compiles and executes the same generated case with normal primitive
specialization and with specialization disabled, while retaining identical dictionary elaboration
and ownership behavior. The resource
profile is deliberately separate from smoke runs, explicitly enables the `FileHandle` generation
type, and uses deterministic file-handle shapes. `Socket` and `TlsSocket` are represented by the
immutable generation type model but stay disabled until deterministic network-resource templates
are added.
The `all` profile deterministically covers every stable profile in each 50-case cycle while limiting
compile, memory-growth, optimization/reuse differential, trait-evidence differential, and
cross-target work to one case each per cycle.
Invalid-source fuzzing deterministically mutates valid generated source
and rotates through checked-in corpus cases, trait declaration tests, general compiler tests,
examples, and parser fixtures. It uses
token deletion and duplication, delimiter replacement, keyword insertion, truncation,
malformed literals, indentation changes, and Unicode insertion. Each mutated parse runs in a
killable child process and asserts bounded diagnostics and crash-free lexer/parser behavior. Native
profiles use child processes with
compiler and program timeouts. Process output is drained without being retained beyond the default
1 MiB per stream, process trees are terminated on timeout or cancellation, and one failure artifact
is capped at 4 MiB. Long runs can stop successfully after a whole-campaign time budget with
`--campaign-timeout` (seconds), and can override the size limits with `--max-output-bytes` and
`--max-artifact-bytes`; replay commands preserve both size limits. The differential profile compares
public `-O0` and `-O2` builds and
also compares normal lowering with the internal debug configuration that disables only in-place
reuse while retaining Perceus ownership. It compares exit status, stdout, and stderr byte-for-byte.
The trait-evidence differential uses the internal
`--debug-disable-trait-specialization` compiler switch solely from the fuzz harness; normal users do
not need this switch, and disabling specialization never removes or changes the selected evidence.
The non-required `fuzz` GitHub workflow runs larger multi-seed campaigns only when manually
dispatched and uploads `artifacts/fuzz` when a case fails; long fuzzing is not a pull-request gate.

Each profile owns its default case count, node budget, compiler and program timeouts, and target
list. CLI values override those defaults, so `run --profile compile` uses the bounded native profile
settings while a long campaign can still set `--cases`, `--max-nodes`, `--target`, per-process
timeouts, and a whole-campaign timeout explicitly. Accepted targets are `host`, `linux-x64`,
`linux-arm64`, `win-x64`, and
`win-arm64`; invalid targets fail during command-line validation even for profiles that do not run
the backend. The `list` command prints each profile's cases, node budget, targets, and oracles.

## Seeds, replay, shrinking, and artifacts

Each case seed is derived solely from the master seed and case index. Generation never uses shared
random state, time, GUIDs, or hash iteration. Replay therefore regenerates canonical source byte for
byte:

```sh
just fuzz-replay 12345 417 perceus
```

Failure reports use the longer executable form and include `--max-nodes`, target, and compiler and
program timeouts. Copy that complete command when a campaign used non-default configuration; these
values are part of exact replay and are also stored in `metadata.json`.
Internal generation failures print the master seed, case seed, case index, resolved profile, and the
same complete replay command, so a late failure in a long campaign remains directly reproducible.

On failure, the runner applies bounded, recursive type-aware candidates and accepts a candidate only
when its stable node/source-size metric decreases, remains valid up to the failing compiler phase,
and the same oracle still fails. Candidates simplify compatible literals and in-scope variables,
branches, collections, records, nested lets, recursive function bodies, top-level function values,
unused top-level declarations and type declarations, unused ADT constructors, redundant exhaustive
match arms, cons inputs, and safe arithmetic children. Complete-program metrics include every
declaration expression,
so removing or simplifying a prelude item is compared consistently with trailing-expression
shrinks. It writes `original.ash`,
`minimized.ash`, `failure.txt`, `metadata.json`, `stdout.txt`, and `stderr.txt` beneath the ignored
`artifacts/fuzz/<stable-id>/` directory. The console also prints the full source and replay command,
case seed, complete generation budget, and selected rule/template IDs, so CI logs remain sufficient
when artifacts are unavailable. Artifact metadata names the exact optimization or reuse comparison
performed by native differential oracles.
Native comparisons reject either side before comparison when compilation or execution times out,
exits non-zero, or exceeds the configured output limit, so two identical crashes cannot pass as an
equivalent result.

Checked-in minimized cases live in `tests/fuzz/corpus` and run with:

```sh
just fuzz-corpus
```

After fixing a failure, review `minimized.ash`, remove any artifact-only assumptions, add it to this
corpus or the closest normal compiler regression suite, and run both the corpus and relevant focused
tests. The runner never commits generated cases automatically.

## Extending the framework

The extension points are intentionally local:

1. To add a generated type, add an immutable case to `Generation/AshesType.cs`, its syntax mapping,
   profile catalog entries, and invariant-test type rendering.
2. To add primitive values for a type, extend `ExpressionGenerator.GenerateLeaf` with bounded,
   deterministic construction and add the matching type-compatible leaf in `FuzzShrinker`.
3. To add an expression form, implement `IExpressionGenerationRule`, declare representative
   `AdvertisedTypes`, register it in `GeneratorRegistry`, and add its feature and trace metadata;
   startup rejects a rule that cannot generate an advertised type.
4. To add a combination, implement `ICombinationTemplate` with generic type preconditions and
   truthful advertised features,
   then register it in `CombinationRegistry`. A template that needs top-level declarations
   implements `IDeclaringCombinationTemplate` and applies only where its entry binding is in scope;
   a leak class derives from `LeakCombinationTemplate` and writes its program as Ashes source.
5. To add an oracle, implement `IFuzzOracle` and register it in `FuzzOracleRegistry`; use in-process
   stable APIs for cheap checks and `CompilerExecution` for isolation or native behavior.
6. To add a shrink rule, add a type-compatible candidate to `FuzzShrinker` and a focused test; every
   accepted candidate must be smaller and preserve
   the failure.
7. To add a profile, register a `FuzzProfile` with explicit types, rules, combinations, oracles,
   context flags, limits, and defaults; startup validation rejects stale IDs and invalid defaults.
8. To add an observable renderer, extend `ObservableValueRenderer.Render`, recursively render every
   payload, include the type in `IsObservable`, and add a parse-and-semantic renderer test.
9. To add a corpus entry, place a canonically formatted minimized `.ash` case in
   `tests/fuzz/corpus` and run `just fuzz-corpus`.

Rule and template IDs are ordinally sorted, duplicate IDs fail immediately, profiles validate their
rule, template, and oracle references at startup, and tests require combination templates to record
every feature they claim.
Case indexes also rotate a deterministic coverage preference across enabled rules and templates;
their ordinary weights remain active, while combination-heavy profiles select a compatible result
type and force the preferred template once per rotation. Template hole budgets reserve node and
depth space for the surrounding shape, so the completed template is not discarded during final
budget validation. Coverage summaries include zero-hit rule and template counts, making incomplete
campaign coverage visible without making replay depend on mutable campaign history. Generated-case
coverage is recorded before its oracles run, and oracle runs are counted when each oracle starts,
including the oracle that reports a failure. Failure output prints that partial coverage summary
before shrinking and artifact persistence.
