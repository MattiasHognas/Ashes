# `--explain` compiler reports

A first-class CLI facility for inspecting the decisions the compiler already made: ownership
contracts, Perceus reference-counting operations, in-place-reuse specialization, and physical
representation. It replaces the environment-variable ownership dump with a structured, filterable,
deterministic report.

This exposes existing decisions. It must not change them.

## Surface

```
ashes compile app.ash --explain ownership
ashes run     app.ash --explain rc
ashes test    tests   --explain reuse
ashes compile app.ash --explain memory
```

Repeatable and deduplicated:

```
ashes compile app.ash --explain ownership --explain reuse
```

An unknown value is a typed CLI error listing the valid ones, never a silent ignore. The option is
rejected on commands where it means nothing — `fmt`, `init`, `add`, `remove`, `install`.

Reports describe Ashes compiler decisions before LLVM lowering. They never interpret optimized LLVM
IR, and they are static: they describe what the compiler decided, not what a run executed.

### Filtering

A selector restricts the report to matching functions, matched against source name, qualified name,
generated label, or owning source function:

```
ashes compile app.ash --explain ownership:Map.set
```

Users must never need an unstable generated symbol name. A selector matching nothing prints a clear
message and succeeds.

### Output

Reports go to **stderr**, so `ashes run app.ash --explain rc` leaves the program's stdout usable.
Nothing is emitted when compilation fails before the report data exists — ordinary diagnostics stay
primary.

The format is human-readable and snapshot-stable: fixed function and section ordering, invariant
number formatting, no timestamps, no addresses, no nondeterministic iteration, no colour when
redirected, and generated functions grouped under the source function they came from.

## The four reports

**`ownership`** — the inferred contracts. Whether a parameter is borrowed, moved, shared or copied;
whether it is proven move-safe; whether the result may alias a parameter or is fresh; whether
internal sharing is possible; whether the analysis failed closed; whether a defensive uniqueness copy
was retained or elided; whether an escape prevented a complete call-site census.

**`rc`** — the Perceus operations in the **final** semantic `IrProgram`, immediately before code
generation: dups, drops, uniqueness checks, allocations, reused allocations, reuse-token production
and consumption, recursive release helpers, copies introduced to establish uniqueness and copies
removed by ownership analysis, generated helpers, and values whose operations were representation-
elided. Counts must describe the IR actually handed to LLVM, so this observes after optimization and
after the state-machine transform — not the freshly lowered IR.

**`reuse`** — the decisions, not a count of instructions: whether a specialization was generated,
which value is the candidate, whether uniqueness is statically proven or dynamically tested, whether
the entry copy was retained or elided, whether layouts are compatible, whether a token is produced
and consumed, whether a fallback allocation exists, and — where the compiler has a concrete reason —
why reuse was rejected. Reasons are stable enum codes; inventing speculative ones is worse than
reporting fewer.

**`memory`** — the correlation, built from the same data as the other three rather than recomputed:

```
ownership fact → Perceus operation → reuse decision → physical representation
```

## Architecture

The boundary that matters: **semantic passes produce facts, reporting formats them, the CLI decides
whether and where to print.** No semantic pass writes to a console. The backend does not own
source-level formatting. Report generation cannot affect generated code — compiling with and without
`--explain` must produce identical bytes.

Most of the data already exists and must be reused rather than re-derived:

- `CompilationDecisionSnapshot` (`Lowering.GetDecisionSnapshot()`) already carries function
  ownership, value placements, reuse decisions, coroutine representations, and pattern bindings, with
  stable origins, dense ordinals, enum reason codes, and no retained AST. It was built for exactly
  this consumer.
- `ReuseDecision` already records decisions at their own decision sites.
- `FunctionOwnershipSummary` is the stable ownership abstraction; expose it through the report path
  rather than handing out mutable analysis dictionaries.

RC reporting is the one part with no existing source: it needs an IR visitor over the final
`IrProgram`, kept out of CLI code and out of scattered switch statements.

## Prerequisites carried over

Two items from the retired Perceus unification plan's deletion gate are still open and land here:

1. **The snapshot is not reachable from the CLI.** `CompilationDecisionSnapshot` and its records are
   `internal` to `Ashes.Semantics`, visible only to `Ashes.Tests` and `Ashes.Backend`. `Ashes.Cli`
   already references `Ashes.Semantics` directly, so this is a visibility decision — widen
   `InternalsVisibleTo`, or make the report model public — not a restructuring.
2. **`ASHES_EXPLAIN_OWNERSHIP` must go.** Three live reads remain
   (`Lowering.OwnershipShadowCompare.cs`, `PerceusLifetimePlacement.cs`, `Lowering.MoveAnalysis.cs`),
   plus documentation in the development guide. After this work no semantic or lowering class queries
   an environment variable; compilation behaviour is controlled through explicit options. Keep it
   only as a deprecated translation into the same request if compatibility demands it, never as a
   second implementation.

The correlation point for `rc` exists already: `src/Ashes.Cli/Program.cs` holds both the `Lowering`
instance and the optimized `IrProgram` immediately before `IBackend.Compile`.

## Acceptance

- All four report types work on `compile`, `run` and `test`; repeated options deduplicate; an unknown
  value is a typed error; help text is updated.
- Ownership reports come from the existing summaries; RC reports inspect the final IR; reuse reports
  explain decisions; memory correlates them from shared data.
- Output is deterministic and filterable, goes to stderr, and leaves program stdout untouched.
- Compiling with and without `--explain` produces byte-identical executables.
- `ASHES_EXPLAIN_OWNERSHIP` is removed or explicitly deprecated, with no remaining semantic reads.
- Tests cover CLI parsing, each report's substance, output destination and stability, and the
  unchanged-codegen guarantee. Existing instruction-counting test helpers fold into the shared
  reporting infrastructure without weakening their assertions.
- Documentation covers the CLI surface and what each report does and does not describe.

## Non-goals

Runtime counters, allocation profiling, heap visualization, LLVM optimization explanations, report
viewers, and JSON output beyond what falls naturally out of the structured model. No changes to
ownership semantics, reuse correctness, resource ownership, or worker-transfer semantics.
