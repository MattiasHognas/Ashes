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
RSS tests cover those lifecycles. The profile is kept out of `just ci-quick` and `just fuzz` because
each case performs three native compilations and executions, but one case runs in every 50-case
rotation of the `all` profile.

List profiles, rules, combinations, and oracles with:

```sh
dotnet run --project src/Ashes.Fuzzing -- list
```

Profiles include `syntax`, `semantics`, `perceus`, `combinations`, `compile`, `differential`,
`memory-growth`, `invalid-source`, `invalid-semantics`, `traits`, `traits-differential`, `async`, `capabilities`,
`resources`, `cross-target`, and `all`. The `traits` profile generates coherent user trait
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
   then register it in `CombinationRegistry`.
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
