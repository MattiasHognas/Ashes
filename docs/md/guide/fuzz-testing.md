# Fuzz testing

`Ashes.Fuzzing` is a standalone property-based compiler-testing application. It is separate from
`Ashes.Tests` so campaigns can own their case counts, seeds, process timeouts, native executables,
shrinking, and artifacts without turning the ordinary unit-test runner into a campaign scheduler.
`Ashes.Fuzzing.Tests` contains only fast deterministic tests of the framework itself.

The generator asks a registry of expression rules for an expression of a required immutable
generation type. Its immutable context tracks typed lexical values and functions, ADTs, records,
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
recursion, nested capability handlers, async capture and deterministic task spawning, Result
propagation and error mapping, and Perceus reuse and
fallback shapes. Targeted Perceus templates distinguish unique record updates, shared
reconstruction fallback, branch-selective reconstruction, and list-tail traversal through a
capturing tail-recursive function. Coverage metadata separately records results that alias inputs,
fresh aggregate results with internal sharing, runtime uniqueness checks, and statically unique
constructor-update paths. Templates may fill their holes with the ordinary generator, so
difficult feature interactions arise compositionally. The initial interaction catalog also
guarantees captured-ADT matching inside closures, closures
inside match branches, recursive list reconstruction, Result pipelines whose continuation is a
generated closure, and handled capability operations performed from a closure selected by a match.
It also guarantees nested matching over a generic tree and a bounded tail-recursive loop carrying
and reconstructing a tree accumulator. These templates remain generic over their payload or result
types.

Every complete generated program also places the result behind
an explicit type annotation, forcing inference to prove the requested generation type. Complete
program generation emits deterministic top-level functions and values, generic ADTs, records,
mutual-recursion groups, and static capability providers in addition to the trailing generated
expression, so parser, inference, lowering, and ownership checks exercise declaration stitching and
sequential top-level scope rather than only isolated expressions.

## Running campaigns

Run the bounded standard suite with:

```sh
just fuzz
```

The pre-commit-sized fixed-seed pass is part of `just ci-quick`. A configurable manual campaign
for a large Perceus run is:

```sh
just fuzz-long -- --profile perceus --cases 100000 --seed 12345 --seeds 4 --max-nodes 120
```

List profiles, rules, combinations, and oracles with:

```sh
dotnet run --project src/Ashes.Fuzzing -- list
```

Profiles include `syntax`, `semantics`, `perceus`, `combinations`, `compile`, `differential`,
`invalid-source`, `async`, `capabilities`, `resources`, `cross-target`, and `all`. The resource
profile is deliberately separate from smoke runs, explicitly enables the `FileHandle` generation
type, and uses deterministic file-handle shapes. `Socket` and `TlsSocket` are represented by the
immutable generation type model but stay disabled until deterministic network-resource templates
are added.
Invalid-source fuzzing deterministically mutates valid generated source
and rotates through checked-in corpus cases, compiler tests, examples, and parser fixtures. It uses
token deletion and duplication, delimiter replacement, keyword insertion, truncation,
malformed literals, indentation changes, and Unicode insertion. Each mutated parse runs in a
killable child process and asserts bounded diagnostics and crash-free lexer/parser behavior. Native
profiles use child processes with
compiler and program timeouts. Process output is drained without being retained beyond the default
1 MiB per stream, process trees are terminated on timeout or cancellation, and one failure artifact
is capped at 4 MiB. Long runs can override these with `--max-output-bytes` and
`--max-artifact-bytes`; replay commands preserve both limits. The differential profile compares
public `-O0` and `-O2` builds and
also compares normal lowering with the internal debug configuration that disables only in-place
reuse while retaining Perceus ownership. It compares exit status, stdout, and stderr byte-for-byte.
The non-required `fuzz` GitHub workflow runs larger multi-seed campaigns only when manually
dispatched and uploads `artifacts/fuzz` when a case fails; long fuzzing is not a pull-request gate.

Each profile owns its default case count, node budget, compiler and program timeouts, and target
list. CLI values override those defaults, so `run --profile compile` uses the bounded native profile
settings while a long campaign can still set `--cases`, `--max-nodes`, `--target`, and timeout
options explicitly. The `list` command prints each profile's cases, node budget, targets, and
oracles.

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
references at startup, and tests require combination templates to record every feature they claim.
Case indexes also rotate a deterministic coverage preference across enabled rules and templates;
their ordinary weights remain active, while combination-heavy profiles select a compatible result
type and force the preferred template once per rotation. Template hole budgets reserve node and
depth space for the surrounding shape, so the completed template is not discarded during final
budget validation. Coverage summaries include zero-hit rule and template counts, making incomplete
campaign coverage visible without making replay depend on mutable campaign history.
