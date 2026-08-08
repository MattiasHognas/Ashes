# Fuzz testing

`Ashes.Fuzzing` is a standalone property-based compiler-testing application. It is separate from
`Ashes.Tests` so campaigns can own their case counts, seeds, process timeouts, native executables,
shrinking, and artifacts without turning the ordinary unit-test runner into a campaign scheduler.
`Ashes.Fuzzing.Tests` contains only fast deterministic tests of the framework itself.

The generator asks a registry of expression rules for an expression of a required immutable
generation type. Its context tracks lexical bindings and feature state, and its explicit node,
depth, collection, recursion, source-size, and combination budgets guarantee termination. A second
validation pass measures complete-program declarations, functions, ADTs, maximum match-arm count,
maximum collection length, recursion complexity, and every expression root in addition to those
recursive generation limits. A case that exceeds any configured dimension falls back to a bounded
typed leaf or fails generation when even the minimum complete program cannot fit. A second registry
contains generic combination templates for sharing, cross-branch aliases, escaping and
shared-capture closures, guarded and ADT matches, list and constructor reconstruction, bounded
recursion, nested capability handlers, async capture, Result propagation, and Perceus reuse and
fallback shapes. Targeted Perceus templates distinguish unique record updates, shared
reconstruction fallback, branch-selective reconstruction, and list-tail traversal through a
capturing tail-recursive function. Templates may fill their holes with the ordinary generator, so
difficult feature interactions arise compositionally. Every complete generated program also places the result behind
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
`invalid-source`, `async`, `capabilities`, `resources`, `cross-target`, and `all`. The resource profile is deliberately
separate from smoke runs and uses deterministic file-handle shapes. Invalid-source fuzzing deterministically mutates valid generated source
and asserts bounded, crash-free lexer/parser behavior. Native profiles use child processes with
compiler and program timeouts. Process output is drained without being retained beyond the default
1 MiB per stream, process trees are terminated on timeout or cancellation, and one failure artifact
is capped at 4 MiB. Long runs can override these with `--max-output-bytes` and
`--max-artifact-bytes`; replay commands preserve both limits. The differential profile compares
public `-O0` and `-O2` builds and
also compares normal lowering with the internal debug configuration that disables only in-place
reuse while retaining Perceus ownership. It compares exit status, stdout, and stderr byte-for-byte.
The non-required `scheduled fuzz` workflow runs larger multi-seed campaigns three times a week and
uploads `artifacts/fuzz` when a case fails; long fuzzing is not a pull-request gate.

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
and unused top-level declarations. Complete-program metrics include every declaration expression,
so removing or simplifying a prelude item is compared consistently with trailing-expression
shrinks. It writes `original.ash`,
`minimized.ash`, `failure.txt`, `metadata.json`, `stdout.txt`, and `stderr.txt` beneath the ignored
`artifacts/fuzz/<stable-id>/` directory. The console also prints the full source and replay command,
so CI logs remain sufficient when artifacts are unavailable.

Checked-in minimized cases live in `tests/fuzz/corpus` and run with:

```sh
just fuzz-corpus
```

After fixing a failure, review `minimized.ash`, remove any artifact-only assumptions, add it to this
corpus or the closest normal compiler regression suite, and run both the corpus and relevant focused
tests. The runner never commits generated cases automatically.

## Extending the framework

The extension points are intentionally local:

1. Add an immutable case to `Generation/AshesType.cs`, its syntax mapping, and leaf construction.
2. Add a primitive or structural `IExpressionGenerationRule`, then register it in `GeneratorRegistry`.
3. Add an `ICombinationTemplate` with generic type preconditions and truthful advertised features,
   then register it in `CombinationRegistry`.
4. Add an `IFuzzOracle` and register it in `FuzzOracleRegistry`; use in-process stable APIs for cheap
   checks and `CompilerExecution` for isolation or native behavior.
5. Add compatible reductions to `FuzzShrinker`; every accepted candidate must be smaller and preserve
   the failure.
6. Add or adjust a `FuzzProfile` using only known registry IDs; startup validation rejects stale IDs.
7. Extend the native observation wrapper when a new result type needs canonical observable output.
8. Add high-value minimized `.ash` cases to `tests/fuzz/corpus`.

Rule and template IDs are ordinally sorted, duplicate IDs fail immediately, profiles validate their
references at startup, and tests require combination templates to record every feature they claim.
Case indexes also rotate a deterministic coverage preference across enabled rules and templates;
their ordinary weights remain active, while combination-heavy profiles select a compatible result
type and force the preferred template once per rotation. Template hole budgets reserve node and
depth space for the surrounding shape, so the completed template is not discarded during final
budget validation. Coverage summaries include zero-hit rule and template counts, making incomplete
campaign coverage visible without making replay depend on mutable campaign history.
