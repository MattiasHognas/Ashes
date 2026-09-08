# Inferred Borrow Lifetimes for Ordinary Values

Status: research proposal with a gated prototype; not an approved replacement memory model.

Baseline: static inspection of `MattiasHognas/Ashes` at commit `0a66bb54cb7b9720fc4b8fdf61980f4281b9d5e9`,
2026-09-08. Revalidate the current implementation before starting. No performance measurements or
runtime validation were performed for this proposal.

## Problem and intended outcome

Ashes already infers ownership and uses Perceus-style RC with reuse. Some references may nevertheless
receive increments, drops or normalization because the compiler cannot prove that an existing owner
remains live for every use of a derived reference.

Investigate whether richer inferred borrow-lifetime relationships can remove a demonstrated class of
unnecessary ownership operations while preserving immutable values, automatic memory management and
the existing conservative RC fallback. The outcome may be an optimization, a narrower proof
improvement, or a documented finding that current analysis already handles the proposed cases.

Do not introduce ownership annotations, Rust-style source lifetime syntax, tracing GC, or new
ordinary-value use-after-move errors. Keep affine resource cleanup separate.

## Research source and evidence limits

William Brandon, Benjamin Driscoll, Frank Dai, Jonathan Ragan-Kelley, Mae Milano and Alex Aiken,
[*Fully-Automatic Type Inference for Borrows with Lifetimes*](https://doi.org/10.1145/3798221), PACMPL
OOPSLA1, published 2026-04-10. [The Morphic project overview](https://morphic-lang.org/publications.html)
describes automatically inferred borrows/lifetimes with reference-count operations inserted where
needed for typing and safety.

The reported benchmark comparison against Perceus makes this a relevant research lead. It does not
establish a speedup for Ashes or prove that Morphic's inference can be inserted into Ashes unchanged.
The abstract and publication details were verified; full-text retrieval was unavailable when drafting
this document. The exact inference rules, assumptions, ownership/reuse interactions and evaluation
methodology must be read before implementing a paper-derived algorithm. No section numbers or theorem
details are assumed here.

## What Ashes already does

[Inspected repository snapshot](https://github.com/MattiasHognas/Ashes/tree/0a66bb54cb7b9720fc4b8fdf61980f4281b9d5e9).

| Area | Existing implementation | Consequence for this proposal |
|---|---|---|
| Function ownership | `src/Ashes.Semantics/OwnershipSummary.cs` stores parameter ownership and result-reach facts | Extend the existing summary only where a concrete missing relation is established |
| Inspect-only analysis | `Lowering.MoveAnalysis.cs` has `ComputeOpenWorldInspectOnlyParams`, an interprocedural fixpoint, and `BorrowInspectOnly` | Ordinary-value inspection and proven hand-offs are not new features |
| Resource borrowing | `Lowering.Borrow.cs` recognizes resource arguments used only through permitted read operations | This is a separate affine-resource contract, not a general ordinary-heap lifetime system |
| Lifetime placement | `PerceusLifetimePlacement.cs` places eligible owners using control flow, liveness and alias information | Integrate proofs with placement rather than deleting pairs by a local syntactic heuristic |
| Provenance and layout | `Lowering.OwnershipProvenance.cs`, `Lowering.TempOwnership.cs`, `Lowering.LayoutCapability.cs` | Borrow legality must retain exact owner/provenance and resolved-layout constraints |
| Reuse | `Lowering.Reuse.cs`, `Lowering.Patterns.cs`, backend `LlvmCodegenMemory.cs` | A zero-count-cost borrow still constrains destructive cell reuse |
| Reports | `CompilationDecisionSnapshot.cs`, `IrExplainReporter.cs` | Record actual proof decisions; distinguish them from final static RC counts |
| Self-hosted counterparts | `selfhost/packages/semantics/src/AshesCompiler/Semantics/OwnershipInference.ash`, `OwnershipSummary.ash`, `PerceusLifetimePlacement.ash` | Check parity independently; do not assume a C# improvement is automatically ported |

The memory model remains hybrid: ordinary escaping graphs use RC while proven scoped values and
specialized runtime state may use regions. Borrow lifetime, graph reference freshness, top-cell
uniqueness, arena reset legality and TCO profitability answer different questions. Do not merge them
into one "safe" flag.

## Research gate: establish the actual opportunity

Before changing code:

1. Read the full paper and relevant implementation/artifact. Summarize its supported language
   fragment, lifetime representation, inference strategy and RC insertion rule, with primary-source
   references.
2. Trace current Ashes ordinary-value call arguments, borrowed/owned results, captures, field
   projections, lifetime placement and reuse eligibility. Compare current self-hosted behavior
   separately.
3. Build a small candidate matrix: repeated read-only helper calls; projections inspected while their
   owner stays live; aliases crossing a branch; higher-order calls; returned aliases; and aliases
   carried across recursion.
4. Compile each supported case with existing ownership/RC/reuse reports and lowered/final IR dumps.
   Record which operation remains, its current decision cause, and whether LLVM later removes it.
5. Identify at least one counterexample to the current precision with a sound proposed proof. If every
   case is already optimized, document that result and choose a genuinely different case or close the
   proposal.

Research completion is a legitimate stopping point. Do not invent an optimization gap to justify
implementation.

## Proposed bounded prototype

Start with monomorphic, statically resolved, non-suspending ordinary-value calls and non-escaping
field/list inspection. Exclude returned borrows, escaping closures, unknown higher-order calls, async
suspension, parallel transfer and handlers with nonlocal control behavior until their lifetime
obligations are explicitly modeled.

### Facts and constraints

Represent a borrow using stable compiler binding/value identities, its owner identity, derived
projection path where needed, its allowed use region and the proof that the owner dominates creation
and remains live through all uses. Source names alone are insufficient because shadowed bindings are
distinct.

Candidate facts include "derived reference depends on owner", "callee does not retain argument" and
"owner must remain live through this use". These are design concepts, not established APIs or a
substitute for the paper's formal system. Reuse existing alias and provenance structures where
possible.

At a control-flow join, preserve a borrowing fact only if it remains justified on every incoming path.
Unknown provenance, unmodeled uses or unproved callee behavior fall back to current ownership
handling. Interprocedural facts must converge conservatively across recursive components.

### Lowering and lifetime placement

For an admitted borrowed use, suppress an otherwise necessary ownership split only when the owner
lifetime covers the borrowed use and cleanup remains balanced. Keep the owner live through the call or
last derived use. Communicate that dependence to lifetime placement so an earlier last-use decision
cannot release the owner prematurely.

Never erase an `RcDup` merely because a matching `RcDrop` exists later: intervening calls, branch
structure, stored children and reuse decisions may depend on ownership.

### Interaction with reuse

Reference count one does not establish permission to overwrite a cell when an uncounted borrow still
observes its old contents. Reject or delay reuse until such a borrow is dead, or retain/materialize
ownership using an explicitly valid fallback. Apply the same rule to old-child destruction and field
transfer.

Keep borrow lifetime and reuse eligibility separate in the recorded facts. Unknown calls must not
receive an uncounted borrow under an ABI that allows consumption or retention.

### Reporting

Extend immutable decision snapshots with the owner relationship, accepted/rejected proof and stable
reason. Useful proposed reasons include unknown callee, escaping use, owner lifetime not established,
incompatible layout and reuse conflict. Capture the facts at the decision site rather than rerunning
mutable analysis while formatting.

Existing `--explain rc` counts final Ashes IR operations before LLVM optimization. Use it for
structural comparisons, not as a dynamic count. If per-pass observation is added independently,
integrate through its supported report interface.

## Implementation checklist

- [ ] Complete the research gate and publish the candidate matrix with current-code evidence.
- [ ] Define a narrow supported fragment and soundness obligations before editing ownership decisions.
- [ ] Add minimal identity-based borrow facts to existing summaries/provenance where justified.
- [ ] Integrate owner-lifetime dependencies into lowering and lifetime placement.
- [ ] Guard reuse, child destruction and fallback materialization against live borrows.
- [ ] Record proof outcomes in ownership observability without changing existing report meaning.
- [ ] Add positive and conservative-fallback regression tests.
- [ ] Measure RC/allocation changes, native runtime, memory behavior and compile-time cost.
- [ ] Port the accepted rule to selfhost with equivalent fixtures once the C# prototype is validated.

## Correctness and performance acceptance

Extend `OwnershipProvenanceTests.cs`, `OwnershipConservativeCauseTests.cs`,
`PerceusLifetimePlacementTests.cs`, `ReuseTokenTests.cs` and report tests as appropriate. Use existing
fuzzing templates and normal/reuse-disabled differential execution to cover interactions. A proposed
borrow-enabled/disabled test mode should compare the new transformation itself if a scoped test
configuration can be introduced cleanly.

The decisive positive case must demonstrate an operation reduction beyond the baseline. Negative cases
must retain safe behavior when a value is returned, captured, stored, shadowed, used on only one
branch, passed to an unknown callee or observed after a candidate reconstruction. Include a parent
with a projected child to catch early parent destruction and a shared value to catch invalid
uniqueness assumptions.

Benchmark both a small case that isolates the removed work and a representative list/tree workload.
Compare fixed compiler settings, inputs and target; report repeated timings and peak memory alongside
IR counts. Fewer increments alone do not justify a change that prolongs large object lifetimes or
prevents more valuable reuse. Set acceptable regression budgets using baseline measurements.

The feature is ready only when a specific precision improvement is demonstrated, conservative cases
retain their previous semantics, memory/reuse tests pass, and implementation claims are tied to actual
evidence. Follow current repository validation gates for any code change.

## First useful task

Find one ordinary-value helper/alias case with an avoidable RC operation, explain exactly why today's
analysis retains it, and write the owner-lifetime proof that would permit removal. This task should
precede introducing a general lifetime solver.
