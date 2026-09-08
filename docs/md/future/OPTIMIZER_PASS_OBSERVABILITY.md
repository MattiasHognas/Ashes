# Optimizer Pass Observability

Status: proposed; no new CLI flags or reporting APIs are implemented by this document.

Baseline: static inspection of `MattiasHognas/Ashes` at commit `0a66bb54cb7b9720fc4b8fdf61980f4281b9d5e9`,
2026-09-08. Refresh paths and pass order against the current compiler before implementation. No
benchmarks or executable equivalence checks were run for this proposal.

## Problem and intended outcome

Ashes can explain ownership/reuse decisions and count RC operations in the final semantic IR. It also
records compiler phase timings. Those surfaces do not by themselves explain which optimizer pass
changed allocation, RC, closure-call or arena behavior.

Add optional observation around existing Ashes optimizer passes. Record stable pass identity,
invocation scope, before/after instruction metrics and optionally elapsed time. Allow a developer to
answer questions such as "which pass removed this closure allocation?" or "did RC-pair fusion do
useful work before LLVM lowering?"

Observation must preserve optimization behavior. This proposal does not replace the custom IR with
MLIR, change pass order, add GPU extraction, or introduce a second optimization engine.

## Research motivation

*JLIR: A Julia-Native MLIR-Inspired Intermediate Representation with Automatic JACC Kernel
Extraction*, submitted 2026-09-04, is a preprint. Its full-text §3.7 describes an ordered pass
manager; §6.6 demonstrates extensible operations, printing and resource estimation.

The relevant idea is to make transformations and their structural effects inspectable within the
compiler's existing implementation language. Per-pass RC/allocation observations are an
Ashes-specific adaptation, not a feature claimed by that paper. Its GPU performance numbers and
Julia-specific parallelism heuristics are not evidence for an Ashes speedup or a safe Ashes
parallelization rule.

## Existing baseline

Browse inspected source.

| Component | Existing behavior | Integration role |
|---|---|---|
| `src/Ashes.Semantics/IrOptimizer.cs` | `Optimize` runs compile-time evaluation, per-function optimization, whole-program closure passes, non-allocation analysis/bracket removal and concatenation folding | Observe the actual pass invocation sites |
| `IrOptimizer.ClosureEnvironments.cs` | Implements interprocedural closure transformations | Include generated/removed functions and stable source lineage |
| `src/Ashes.Frontend/CompilePhaseTiming.cs` | Supplies existing phase timing support | Reuse timing concepts without introducing global mutable observers |
| `src/Ashes.Semantics/CompilationDecisionSnapshot.cs` | Captures immutable lowering decisions | Keep decision explanations separate from instruction-count differences |
| `IrExplainReporter.cs`, `ExplainReport.cs`, `ExplainReportFormatter.cs` | Existing compiler reports; RC metrics read final semantic IR | Add a compatible report section after documenting the contract |
| `IrTextFormatter.cs`, `IrFunctionSelector.cs` | Existing IR rendering and function selection | Reuse formatting and stable selector semantics |
| `src/Ashes.Backend/Llvm/LlvmCodegen.cs` | `RunLlvmOptimizationPasses` skips LLVM passes at O0; selects `default<O1/O2/O3>` otherwise | Define a clear pre-LLVM boundary for this feature |
| `selfhost/packages/semantics/src/AshesCompiler/Semantics/IrOptimizer.ash` | Self-hosted optimizer implementation exists | Later emit the same logical schema where pass coverage matches |

Ashes optimization already runs before LLVM. `-O0` does not disable the Ashes optimizer. Existing
`--emit-ir lowered|final` and `--explain ownership|rc|reuse|memory` should retain their present
meaning.

## Scope and design

### Observation boundary

Implement a compilation-scoped optional observer or equivalent collection mechanism in `Semantics`. A
proposed API shape is `Optimize(program, observer)` with the existing call remaining valid; the exact
API must fit the current public/internal boundary.

Observe transformations where they actually execute. Do not replay them for reporting. Capture
`before` metrics before invoking a pass, since a pass may mutate instruction collections in place. An
immutable record pointing at a mutable list is not a valid `before` snapshot.

Keep the existing disabled path cheap: avoid scanning IR, formatting, hashing or allocating metric
snapshots unless observation is requested. Report collection must not affect temporary IDs, generated
labels, source lineage or optimization caches. Parallel compilations must have independent sinks.

### Stable event model

Use a versioned data model with at least the following fields. Names are proposed, not existing APIs.

| Field | Meaning |
|---|---|
| `passId` | Stable logical transformation identifier, independent of display wording |
| `invocation` | Deterministic sequence within one compilation |
| `scope` | Program or function, with a stable function identity where applicable |
| `iteration` | Fixpoint iteration when individually observed |
| `before` / `after` | Immutable metric vectors |
| `addedFunctions` / `removedFunctions` | Explicit function-set changes for whole-program passes |
| `elapsed` | Optional timing, excluded from deterministic snapshots |
| `status` | Completed, skipped or failed, with a concise reason where known |

Function attribution must follow existing generated-function/source-origin metadata. A specialized
clone must not be conflated with its parent or lost because its label is absent from the original
program. Report totals over the union of affected functions, treating an absent side as zero where
appropriate.

### Metrics

Count instructions by explicit semantic category and retain raw categories where aggregation would
mislead:

- RC dup/drop instructions, separating runtime-managed operations from erased markers where IR
  distinguishes them.
- Uniqueness checks and reuse-token production/consumption instructions.
- Ordinary heap, runtime-RC and stack allocation sites as separate categories.
- Direct calls, indirect closure calls and closure-construction sites.
- Arena save/restore/reclaim instructions and total instruction/function count.

Audit every contributing `IrInst` shape against `Ir.cs` and current backend handling. For example, a
conditional `AllocReusing` path is neither a guaranteed allocation nor a guaranteed saved allocation.
A string helper can allocate without appearing as a generic allocation instruction. Name these metrics
"sites" or "instructions"; do not present them as complete heap-allocation totals.

Keep static counts distinct from executed operations, bytes allocated and peak RSS. Neither a removed
instruction nor fewer sites establishes a runtime speedup.

### Pass coverage and ordering

Start with whole-program boundaries already visible in `Optimize`. Then instrument individual local
passes, including repeated cleanup and fixpoint passes, at their real call sites. Preserve the exact
existing order, termination conditions and cache lifetimes.

Choose one documented aggregation rule: either show each fixpoint iteration or provide the combined
before/after event with an iteration count. Avoid counting both nested and parent deltas as separate
contributions to a cumulative total. A pass's delta describes its immediate effect; it does not
measure causal importance, since an earlier pass may enable a later change.

### Reporting and optional dumps

Propose `--explain passes[:selector]` through the existing reporting infrastructure, subject to the
current CLI specification and selector rules. Update the CLI reference before implementing the new
surface. Verify how compile, run and test pipeline modes request and propagate reports. A pipeline
that intentionally skips the optimizer should explicitly report that fact rather than invent pass
events.

Default human-readable output should be concise and deterministic. Timings should be explicitly
requested or clearly separated from stable output. Continue sending compiler diagnostics/reports to
the existing report destination without contaminating program stdout.

Full per-pass IR dumps are a later extension: collect only selected passes/functions, bound output
size, and state when truncation occurs. A bounded summary or IR fingerprint must not be advertised as
proof of semantic equivalence.

## Implementation sequence

- [ ] Inventory current pass invocation sites, mutation behavior, fixpoints, timing hooks and report
  plumbing.
- [ ] Define metric semantics and test their classification against representative instructions.
- [ ] Add a versioned immutable observation record and compilation-scoped sink.
- [ ] Observe whole-program boundaries with no changes to ordering or enabled behavior.
- [ ] Instrument individual local passes and fixpoint iterations with deterministic scope identities.
- [ ] Add documented CLI/report integration and source-function filtering.
- [ ] Test disabled/enabled equivalence, concurrency isolation and generated-function attribution.
- [ ] Measure disabled-path overhead and bounded enabled-mode overhead on representative programs.
- [ ] Add self-hosted report parity where corresponding passes exist; document differing pass
  coverage.
- [ ] Consider bounded selective dumps only after metric reporting is useful and stable.

## Validation and acceptance criteria

Extend `IrOptimizerTests.cs`, `ExplainReportTests.cs` and `ExplainReportCliTests.cs`. Select existing
optimizer regression inputs with known transformations, such as closure devirtualization, RC-pair
fusion, arena-bracket removal or concatenation fusion. Assert a meaningful before/after delta at the
responsible pass and an unchanged final optimized IR.

Include a pass that mutates a list, a no-op pass, a repeated/fixpoint pass and a whole-program pass
that introduces a specialized function. Test source selectors against generated lineage. Compile
independent programs concurrently to detect shared observer state.

For deterministic report tests, omit timings. Verify identical final semantic IR and native program
behavior with observation disabled and enabled. If binary comparison is used, control unrelated
timestamps/build metadata; do not accept uncontrolled binary inequality as proof that observation
changed semantics.

Check that reported final RC instruction counts agree with the existing RC report when using the same
classification, selected functions and pipeline stage. Explain any intentionally different metric
instead of silently combining it.

Acceptance requires stable pass IDs, accurate immutable before/after measurements, preserved optimizer
behavior, explicit stage boundaries, correct source attribution, and no significant disabled-mode
regression under an agreed measured budget. A small benchmark should demonstrate how the report helps
locate an actual optimization change. Follow current repository validation gates when implementing.

## First useful task

Add an internal optional observer around compile-time evaluation and the whole-program closure phase.
Use one existing closure optimization test to prove that `before` metrics are captured correctly,
final IR remains identical and reports are isolated between compilations. Expand to individual passes
only after this foundation works.
