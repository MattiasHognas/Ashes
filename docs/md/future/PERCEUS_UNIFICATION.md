# Perceus Ownership Unification — Design Document

Status: implementation in progress. Produced from four parallel code audits (core data types;
closures & loop-carried/TCO values; async/task-frame values; capability/handler & request-local
values), synthesized here. Phase 0 (extending `FunctionOwnershipSummary` with expression-level
freshness and closure-forwarding provenance, `#303`), Phase 1 (Strings/Bytes/BigInt
builtin-freshness metadata, `#302`), Phase 2 (capability RC-eligibility gate narrowing, `#301`),
Phase 3 (closure/function-result provenance unification + borrow-forwarding fix, `#308`),
Phase 4 (ADTs and Tuples per-expression top-cell freshness, unifying `ProducesFreshRuntimeManageableAdt`/
`IsFreshRuntimeManageableAdtExpression`/`IsFreshConstructorTree`/`ProducesFreshTuple`, `#309`), and
Phase 5 (Lists per-expression top-cell freshness + a reused-cell reuse-token/RC bookkeeping fix) have
all landed on `main`. Phase 6 (TCO loop-carried values) was first attempted and recorded below as an
investigation with no landed change (two independently-derived fixes were each confirmed, by direct
measurement or by the existing regression suite, to make things worse, and both were reverted rather
than shipped) — a follow-up session then closed the specific gap that investigation diagnosed
(interprocedural call-result freshness), landing a real fix: fannkuch-redux N=11 peak RSS dropped from
the ~27.4GB regression baseline to ~1.56GB (a 17.6x reduction), fully validated. A second follow-up
session closed the remaining ~1.56GB residual too (a match-scrutinee wrapper-cell drop that a
shared-ownership-identity bug had been silently skipping): fannkuch-redux N=11 peak RSS is now 256KB
at every N from 8 through 11 — constant, matching the pre-regression historical baseline, not merely
reduced. A third follow-up session then attempted the four-classifier collapse and all-or-nothing
frame veto removal Phase 6 was originally titled for, and again landed no change: the veto was
confirmed to fire on real, currently-shipped code (not just constructed examples), removing it was
proven SAFE at every scale tested (per-parameter mixing across unrelated types, and a direct
adversarial same-type-parameter-swap test, never produce a wrong value or a crash), but two of the ten
real `challenges/` programs (`1brc`, `reverse-complement`) measurably regressed in peak RSS — a
previously-unrecognized PROFITABILITY cost (a per-iteration defensive copy the arena-only frame never
pays), not a safety one — and two successively narrower attempts to keep part of the removal safe were
each found insufficient or inert. All three attempts were reverted in full; see "Phase 6, third
attempt" below for the complete account, including a newly-discovered, separate latent gap in the
closure-eligibility check uncovered along the way. Two follow-up sessions then ran in parallel: one
built the promotion-cost signal the third attempt's own closing note called for — a per-parameter
profitability query (`Lowering.GetTcoPromotionProfitability`), validated against the third attempt's
two real regressions plus its synthetic adversarial case plus a single-heap-parameter sanity check,
landed as read-only analysis infrastructure with no compiled-output change and not yet wired to any
decision; see "Phase 6, promotion-cost signal" below. The other closed classifier (D)'s own timing gap
in isolation (a direct pattern binding off a TCO parameter that independently escapes is now
protected) and confirmed the closure-eligibility gap is not safely fixable without a larger retrofit —
see "Classifier (D) timing gap — landed; closure-eligibility dead code — investigated, left unfixed"
below. A fifth session then wired the promotion-cost signal into the frame veto itself — the veto's
firing condition is unchanged, but its consequence is no longer a blanket clear: only a parameter the
signal finds `NotProfitable` is demoted, so a sibling parameter the signal finds `Profitable` keeps its
runtime-managed representation even when the frame can never fully arena-reset — but only after fixing a
real gap the signal itself had (found by re-validating against the actual `challenges/` programs, not
just hand-built unit tests) and separately discovering, unrelated to anything in this document's own
changes, that the classifier (D) timing-gap fix regressed fannkuch-redux's peak memory off its 256KB
floor; see "Phase 6, profitability-gated veto relaxation" below for both accounts. The four-classifier
structural collapse remains unstarted, and the classifier (D) regression remains unfixed. Phase 7 is not
yet started.

**Correction to the classifier (D) timing gap fix, made after that session's own merge**: the fix
landed above shipped a live regression on `fannkuch-redux` specifically — the exact benchmark this
whole document exists to keep constant. A direct pattern binding fed into a plain (non-self,
non-constructor) helper call was wrongly classified as escaping, and the resulting protective dup was
never balanced by a matching drop for this shape, growing N=8's 256KB floor to ~54MB and N=9's to
~554MB (extrapolating to tens of GB by N=11) — not the "harmless, at worst redundant" tradeoff that
fix's own writeup assumed. See "Classifier (D) escaping-call-argument gap — landed" below for the
full diagnosis and fix; the four disassembly-identical benchmarks that session checked did not
include `fannkuch-redux` in that specific check, which is why it was missed at merge time.

Known follow-ups from Phase 0, not yet landed: confirming the shadow-compare result at the full
`challenges/fannkuch-redux` N=11 workload (only N=9 was run, for sandbox resource-safety reasons);
teaching `FunctionResultProvenance` to consult Phase 1's `BuiltinRegistry.ProducesFreshRcResult`
metadata so a function forwarding to a builtin string/bytes/bigint producer is also recognized as
RC-eligible (currently under-recognized — a real, understood, not-yet-closed gap, not a defect).

Phase 4 follow-up: the "top-cell fresh" notion this session's own checkpoint anticipated (a
narrower sibling of Phase 0's `ExpressionFreshness` — "was this specific outer cell freshly
allocated" rather than "does nothing anywhere in this value alias a parameter") turned out to be
purely syntactic (no interprocedural alias reasoning needed to answer "is this expression literally
a constructor/tuple application"), so it is implemented as a standalone recursive query
(`Lowering.TopCellFreshness.cs`) rather than folded into the `ResultReach` fixpoint — routing it
through that fixpoint would have narrowed its domain to only `_maFuncs`-registered functions, a
regression versus the ad hoc classifiers it replaces. One design assumption from this phase's own
checkpoint was tried and found wrong during implementation, not just proposed: tuples do NOT share
the ADT CO-38/PR #299 same-parent-type reconciliation hazard (a tuple type has exactly one shape, and
the ambient RC-tuple flag only ever gates literal `TupleLit` allocations actually present in the
lowered body, never a passthrough binding) — an AND-based reconciliation attempt for
`ProducesFreshTuple` broke two live regression tests before being reverted to its original
existence-check (OR) semantics. Whoever picks up Phase 5 (Lists) should not assume the ADT
reconciliation pattern transfers to every category without re-deriving whether the same hazard
actually applies.

Phase 5 follow-up: the audit's characterization of Lists ("ambient flag + reactive backward scan")
turned out to describe only part of the real pipeline once re-checked against the current code —
`LowerRuntimeManagedListElement`/`LowerCons` already did Tuple-style forward, per-element AST
classification before this phase started; the genuinely ambient/reactive pieces were narrower:
`IsFreshListConstructionExpression` (no control-flow transparency) and `IsRuntimeManagedResultTemp`'s
final "did this temp actually land RC" check (used symmetrically by both List's and Tuple's per-element
predicates — Phase 4 did not eliminate it for Tuples either, so Phase 5 left it alone for Lists too,
rather than making the two categories diverge for no proven benefit).

**Correction, made after the checkpoint report on this exact question**: this note originally claimed
Lists do not share the ADT AND-reconciliation hazard, reasoning that a list has no sibling-constructor
identity for an AND-style reconciliation to key on. That structural argument was real but incomplete —
it explains why there is no "different constructor of the same declared type" hazard, but missed a
DIFFERENT mixing hazard that does not need one: `if cond then existingList else [fresh, list]`, one
arm a bare-Var passthrough of an existing (not provably fresh) binding, the other a genuine fresh
construction. An existence check (OR) sets the ambient RC-eligibility flag for the whole escaping
position, so the fresh arm's cons cell is allocated on the RC heap (`Alloc RuntimeManaged: true`) —
but the join-level `MarkUniformRuntimeManagedResult`/`MarkRuntimeManagedMatchResult` machinery (which
decides whether the JOINED/matched value is actually treated as owned) requires every arm
independently verified runtime-managed, which the passthrough arm never is. Direct IR inspection
confirmed the two mechanisms disagree (an orphaned `Alloc{RuntimeManaged: true}` with no reachable
`RcDrop`) — a real, provable bookkeeping defect, fixed by requiring every terminal independently fresh
(AND, not OR) so the two verdicts can no longer disagree. **What was NOT confirmed**: an actual
process-memory leak. Compiled-binary testing at real scale (a discarding loop and a consuming loop, up
to 50M iterations; a larger-payload variant to make any real leak visible in RSS) found no measurable
difference between the pre-fix and post-fix binaries — the orphaned cell appears to be reclaimed by
the same scope-based bulk arena watermark reset that reclaims ordinary arena garbage, since it never
escapes its own allocating scope before that reset runs. Whether that reclaim path holds in every
context this shape could arise in was not re-derived from the codegen/runtime source in the time
available. The fix is real and unambiguously correct (it cannot make anything less safe, and costs
nothing) but should be read as "a provable bookkeeping defect worth removing outright," not "a leak
this phase's own testing caught live" — the two are different claims, deliberately not conflated. The
same latent gap, by the same reasoning, appears to reproduce for ADTs/Tuples' `AnyArmConsistentlyFresh`
too (a bare-Var sibling has no constructor-identity group key, so it is excluded from reconciliation
exactly like a funneling recursive call) — checked only at the IR level, not independently confirmed
at the compiled-binary level, and not fixed here since that is already-shipped, more broadly-used code
this phase was not scoped to touch; reported separately for whoever owns that decision.

The tail-sharing safeguard this phase needed and neither ADT nor Tuple
did: `IsFreshListConstructionExpression`'s terminal set (`ListLit`, or a `Cons` chain bottoming out in
one) was extended with control-flow transparency (via `CollectFreshEscapeTerminals`, as
`ProducesFreshRuntimeManageableList`) but deliberately NOT widened to accept `Expr.Call`/`Expr.Var` —
the arena/TCO side already has a laxer sibling (`IsFreshListRebuildExpr`) answering a different
question (safe to whole-clone at a back-edge, CO-32's concern) and the two must not be merged; keeping
the terminal sets disjoint is what makes a threaded/shared-tail list structurally unable to be
RC-promoted by this engine, independent of which call site asks. Separately, reading `LowerConsCell`
closely surfaced a real, pre-existing, latent bookkeeping bug (not introduced by this phase): a list
cons cell satisfied by a reuse token is unconditionally an arena `AllocReusing` (`RuntimeManaged:
false` always — there is no list-specific runtime-managed reuse cleanup), but the post-emission
bookkeeping that marks a cell `_runtimeManagedResultTemps` keyed only off the `runtimeManaged` request
flag and `_runtimeRcTcoListTailBinding`, independent of which branch actually emitted the cell — so a
reuse-token hit could mark an arena-reused cell (no RC header) as runtime-managed anyway. Fixed by
keying the bookkeeping directly off which branch ran; this IS this phase's reuse-token reconciliation
for Lists (there is no separate RC-managed list reuse-token path to reconcile against — the RC reuse
proof, `TryGetRuntimeManagedReuseScrutinee`, is gated to `TypeRef.TNamedType` and categorically
excludes `TList` — so "reconciling the two proofs" resolved to "there is only one real proof for
Lists, and its bookkeeping must not disagree with its own emission decision"). No `.ash`-level program
was found that reaches the vulnerable state within this session's budget (it needs a reuse token and
the narrow list-tail-extension binding to coincide), so the fix is verified by full-suite regression
(zero behavior change across 1697 C#/52 LSP/541 e2e tests) and by inspection, not by a dedicated
fails-without-the-fix repro — flagged honestly rather than claimed as fully pinned.

## 0. Objective (restated)

Make Perceus the single semantic ownership model for every ordinary immutable heap value. Region
allocation, stack allocation, in-place reuse, and RC-as-runtime-representation become optimizations
chosen *after* ownership (owner, dup/drop sites, uniqueness, reuse-legality) is already established —
not parallel ownership models the compiler arbitrates between per call site. Resources (`File`,
`Socket`, `Process`), scheduler bookkeeping, native runtime state, stack scalars, and foreign
allocations (PCRE2, mbedTLS) are unaffected and out of scope.

## 1. Current architecture

The RC-Perceus migration (`a8c9ca5`, landed 2026-07-23, documented in
`docs/md/internals/changelog.md`'s "RC Perceus migration chronology") states its intent plainly:
RC is "the general lifetime substrate for escaping ordinary heap graphs," and arena is retained only
for "compiler-proven scoped values." **The audits found this is not what the implementation actually
does.** There is no single gate that decides ownership once and then picks a representation.
Instead, for essentially every value-producing expression, `Lowering.cs`/`Lowering.Ownership.cs`
run a **bespoke syntactic classifier** that re-derives "is this fresh / is this safe to promote"
from the shape of the AST node that produced the value — not from any persistent, computed-once
ownership fact.

Concretely, the audits catalogued the classifier families:

| Value category | Classifier family | Approx. count | File |
|---|---|---|---|
| ADTs | `CanRuntimeManageAdt`, `CanRuntimeManageCopyAdt`, `..GenericCopyAdtConstructorApplication`, `..FreshHeapChildAdtConstructorApplication`, `..OwnedChildAdt(ConstructorApplication)`, `..RecursiveCopyAdt`, `IsFreshConstructorTree`, `IsFreshRuntimeManageableAdtExpression`, `ProducesFreshRuntimeManageableAdt` | ~10 | `Lowering.Ownership.cs`, `Lowering.cs` |
| Lists | `_runtimeRcListAllocationRequested` (ambient flag) + `IsRuntimeManageableListElement` + `IsRuntimeManagedResultTemp` (reactive scan) + `IsFreshListConstructionExpression`/`IsImmediateCopyListMatchUse`/`TryGetRuntimeRcListTailExtension` | ~6 | `Lowering.Patterns.cs`, `Lowering.cs` |
| Tuples | `ProducesFreshTuple`, `CanRuntimeManageFreshTupleExpression`, `CanRuntimeManageOwnedTupleType` — near-duplicates of the ADT-field dispatch | 3 | `Lowering.Ownership.cs`, `Lowering.cs` |
| Strings | `IsRuntimeRcStringProducer` — qualified-call-name whitelist | 1 | `Lowering.cs` |
| Bytes | `IsRuntimeRcBytesProducer` + `IsRuntimeRcClosureCaptureSafeBytesProducer` + `IsArenaAllocationFreeBytesOperand` + view-exclusion `RuntimeManagedTcoListElementContainsBytes` | 4 | `Lowering.cs`, `Lowering.Ownership.cs` |
| Closures (result/argument RC metadata) | `_functionReturnedClosureLabels`/`_runtimeManagedFunctionResultLabels`/`_knownFunctionLabelsBySlot`, populated by a **backward instruction scan** (`RecordFunctionResultProvenance`/`RecordReturnedClosureLabel`) | 1 system, whole-program side table | `Lowering.cs` |
| TCO loop accumulators | (A) `TcoContext.RuntimeManagedParamSlots` (whole-loop, all-or-nothing, type-shape-driven) + (B) `GetTcoCopyOutKind`/arena `TcoBackEdge*` machinery (CO-28/29/32/34/35/36, independent of A) + (C) `_runtimeManagedResultTemps` (temp-grained) + (D) `_runtimeManagedTcoPatternAliases` (pattern-derived-value grained, PR #298) | **4 independent classifiers**, reconciled only by hand-sequenced dispatch order | `Lowering.Types.cs`, `Lowering.cs`, `Lowering.Ownership.cs` |
| Async/await-captured values, task frames | `_usesAsync`/`_inCoroutineBody` — two **whole-program/lexical-scope booleans**, set once before lowering starts, gating ~12+ call sites to force arena treatment regardless of whether the specific value ever crosses a suspend point | 2 flags, program-wide | `Lowering.cs:819-823`, consumed throughout |
| Capability/handler environments | `CapabilityGlobalCount > 0` — a **whole-compilation-unit** count (not per-function, not per-value), gating the *same* ~15 call sites as the async flags | 1 flag, program-wide | `Lowering.Capabilities.cs:364` |

A genuinely Perceus-shaped, general-purpose ownership analysis **already exists**:
`Lowering.MoveAnalysis.cs`'s `FunctionOwnershipSummary` (`OwnershipSummary.cs`) computes
consumed/borrowed parameters, unique parameters, and result-freshness/reach via a real interprocedural
fixpoint, and its own doc comment calls it "the stable bridge between today's move/reuse analyses and
the owned and borrowed environments used by RC Perceus." **It is consulted at only 4-5 call sites in
the entire compiler** (fold-reuse copy elision and TCO-helper-inlining eligibility) — never by
`LowerVar`'s runtime-managed decision, never by closure-capture-ownership decisions, never by
`TcoContext`'s classification. The "bridge" is aspirational in its own doc comment, not actual in
the code. This is the single clearest, most concrete lever for the proposed redesign (§5).

Codegen itself (`src/Ashes.Backend/Llvm/LlvmCodegenMemory.cs`) is comparatively trivial: an
`EmitAllocAdt(..., runtimeManaged)`/`EmitRuntimeRcAlloc` vs. bump-pointer `EmitAlloc` choice keyed on
one boolean baked into the IR during lowering. All of the actual complexity — and all of the actual
risk — is upstream, in `Ashes.Semantics`.

```mermaid
flowchart TB
    subgraph today["Current architecture (as audited)"]
        AST[AST / expression]
        AST --> C1["~10 ADT freshness predicates"]
        AST --> C2["List ambient-flag + reactive scan"]
        AST --> C3["Tuple freshness (duplicated ADT logic)"]
        AST --> C4["String producer whitelist"]
        AST --> C5["Bytes producer whitelist + view exclusion"]
        AST --> C6["Closure provenance backward-scan"]
        AST --> C7["TCO: 4 independent classifiers (A/B/C/D)"]
        C1 & C2 & C3 & C4 & C5 & C6 & C7 --> GATE{"_usesAsync OR\n_inCoroutineBody OR\nCapabilityGlobalCount>0 ?"}
        GATE -->|any true, program-wide| ARENA[Force arena]
        GATE -->|all false| PERVALUE[Per-value classifier result]
        FOS["FunctionOwnershipSummary\n(genuinely Perceus-shaped)"] -.->|consulted at only\n4-5 call sites| MINOR["Fold-reuse elision,\nTCO-helper inlining only"]
    end
```

## 2. Ownership flow

Ownership decisions are made **at lowering time, interleaved with Hindley-Milner unification**, at
each of several distinct "escape points": a function result, a `let`-scope exit, a TCO back-edge, a
closure capture, a match-arm result, a call argument/result boundary. Because a value's type is often
still an unresolved type variable when its producing expression first lowers (unification with later
call sites hasn't happened yet), several of these classifiers cannot decide inline and instead record
a *pending* decision (`PendingTcoReset`, `IrInst.TcoResetPending`, `_pendingNestedTcoPatternAliasSites`,
`_pendingRuntimeArgumentFlags`) that gets resolved and spliced back into the instruction stream later,
independently, by its own resolver pass. **Each of these deferred-resolution tables can reach a
different conclusion than a sibling table would for the same underlying value** — this "record now,
resolve independently later" pattern is directly implicated in two of the historical bugs found by
the closures/TCO audit (CO-34(b)'s late-typed-accumulator leak, and PR #298's nested-pattern-alias
use-after-free).

Three **whole-program** gates sit on top of all of this and can override any per-value classification
outcome: `_usesAsync`, `_inCoroutineBody`, `CapabilityGlobalCount > 0`. All three collapse to the
same effect at nearly the same ~15 call sites (`TryEmitScopeCopyOut`, `LowerCallCopyOutResult`,
`MakeClosure`'s `ReturnsRuntimeManaged`, `TrackRuntimeManagedMatchScrutinee`, etc.): force arena
(`ArenaScopeBoundary`/`ArenaCallBoundary`) instead of RC (`RcNormalization`), **for every value in
every function**, not just values that plausibly interact with async/capabilities. `_usesAsync` and
`CapabilityGlobalCount` are computed once, before lowering starts, from a whole-program pre-scan —
they are never narrowed per function or per value.

Provenance is frequently tracked **reactively** rather than forward-propagated: `IsRuntimeManagedResultTemp`
(the single most load-bearing predicate in the compiler for this question) performs a **linear
backward scan of every IR instruction emitted so far** looking for an `AllocAdt`/`ConcatStr`/etc. with
`RuntimeManaged: true` targeting the temp in question. Closure result/argument provenance
(`RecordFunctionResultProvenance`) does the same kind of backward scan for `MakeClosure`. Both are
provably incomplete for a value that reaches its use site *by forwarding through a chain of calls*
rather than by direct construction — exactly the shape that (per this session's own investigation)
caused the fannkuch-redux regression's dominant remaining leak.

```mermaid
sequenceDiagram
    participant AST
    participant Classifier as Per-expression classifier
    participant Gate as Whole-program gate
    participant Emit as IR emission
    AST->>Classifier: expression shape (type may be unresolved)
    Classifier->>Classifier: re-derive freshness/safety from AST shape
    alt type unresolved
        Classifier->>Classifier: record pending decision (own table)
    end
    Classifier->>Gate: proposed ownership (RC vs arena)
    Gate->>Gate: _usesAsync / _inCoroutineBody / CapabilityGlobalCount check
    alt any gate true
        Gate->>Emit: force arena, regardless of classifier's answer
    else
        Gate->>Emit: use classifier's answer
    end
    Note over Classifier,Emit: Deferred tables resolved independently,\nlater, possibly disagreeing with siblings
```

## 3. Storage flow

Once a value is classified, storage is mechanical and largely uniform:

- **Arena**: bump-pointer allocation (`EmitAlloc`) into a growable-chunk arena; reclamation is a
  scope/TCO-back-edge watermark reset (`RestoreArenaState`/`ReclaimArenaChunks`), never a per-value
  operation.
- **RC**: 16-byte header + payload (`EmitRuntimeRcAlloc`), refcounted via `RcDup`/`RcDrop`, backed by
  per-thread dense chunks plus exact-size free-list bins for small cells, direct OS return for large
  cells.
- **Stack** (closures only): `MakeClosureStack`, lexically scoped.
- **Borrowed view** (Bytes only): `BytesSubView`, tied to a resource/mmap region, never RC or arena
  — a genuine third representation, discussed in §7.
- **Task/scheduler region**: a task's state struct is a single arena allocation with bulk (not
  per-value) reclamation on completion.

The representation layer is, with one exception (Bytes views, §7), **already generic enough to
accept either model without change** — `EmitCreateTaskCopyCaptures`, for instance, does a raw
word-for-word bit copy into a task frame with zero ownership awareness; it would copy an RC pointer
exactly as happily as an arena pointer. This confirms the target architecture's core premise:
representation choice is not, today, the hard part. Ownership *decision-making* is.

## 4. Places where they are coupled today

The four audits together catalogued **~50 distinct call sites** where an ownership-model distinction
changes **which IR instructions get emitted** — not merely which allocator a value lands in. The full
per-site detail lives in the four audit transcripts (available on request); the load-bearing pattern,
repeated across every category, is:

1. A boolean ownership classification (`RuntimeManaged`, "is this a known function label",
   "is this param in `RuntimeManagedParamSlots`") determines **which of two structurally different
   lowering functions runs** (e.g. `LowerEscapingResult` vs. plain `LowerExpr`; `TryEmitScopeCopyOut`'s
   `RcNormalization` vs. `ArenaScopeBoundary` branch; `EmitTcoBackEdgeArenaBlock`'s three-way
   first-match dispatch).
2. That, in turn, decides **whether an explicit drop/dup is emitted at all**, and for which values
   (RC ADTs get a recursive `RcDrop` walk per child; arena ADTs get an implicit no-op watermark
   reset — same source construct, different instruction sequences).
3. In closure/call-boundary cases, it changes **the calling-convention ABI bit itself**
   (`ClosureResultOwnershipBit`/`ClosureArgumentOwnershipBit`), not just a local decision.
4. For TCO loops specifically, an all-or-nothing veto (`LowerLambdaCoreRejectPartialRuntimeManagedTcoFrame`)
   means **one hard-to-classify accumulator silently changes the emitted code for every other
   accumulator in the same loop**.
5. For async/capability values, a **whole-program** flag overrides every per-value classifier's
   answer at once, for every function, regardless of whether that function's values are anywhere
   near an `await` or a `handle`.

Two coupling points deserve special call-out because they are the cleanest and the murkiest,
respectively:

- **Cleanest**: `Lowering.Capabilities.cs:364`'s `CapabilityGlobalCount`. A program that declares a
  capability and resolves every use of it statically via `provide` (zero dynamic dispatch anywhere)
  still pays the full program-wide RC-disablement cost, for every value in every function, forever.
  No documented reason was found for bundling this with the async/coroutine exception; the one
  in-code comment justifying the bundling (`Lowering.Patterns.cs:929-934`) explicitly reasons about
  *only* the async/coroutine half.
- **Murkiest**: `PerceusLifetimePlacement.cs`'s CFG builder has no notion of `Suspend`/`Resume` at
  all. For any value that *does* get classified `RuntimeManaged=true` and whose lexical scope spans
  an `await`, the drop-placement pass's forward-reachability search cannot see past a coroutine
  state boundary (each state is a disconnected subgraph, dispatched only via a `SwitchTag` header),
  so it places the `RcDrop` right after the pre-suspend save — even when the value is read again
  after resume. **No test in the entire suite exercises this interaction**: the state-machine
  transform's own unit tests call `StateMachineTransform.Transform` directly and never invoke
  `PerceusLifetimePlacement`; every async e2e fixture captures only `Int` values across an `await`,
  never a `Str`/`List`/ADT. This is very likely why the bug hasn't surfaced: the *other* coupling
  (the whole-program `_usesAsync` gate) currently prevents async-touched heap values from ever
  reaching RC in the first place, which incidentally also prevents this CFG-blindness bug from firing.
  **These two bugs currently mask each other.**

## 5. Proposal for separating ownership from representation

The unifying move all four audits converge on: **extend `FunctionOwnershipSummary` (and the
fixpoint that computes it) into the one thing every category's classifier queries**, instead of each
category re-deriving freshness/uniqueness from AST shape independently. Concretely, four extensions
to the existing (already-Perceus-shaped, already-interprocedural) analysis:

1. **Expression-level, not just whole-function-level.** Today the summary answers "is this
   function's *result* fresh" — the ADT/tuple/list/match-arm classifiers need "is *this
   sub-expression* fresh," at arbitrary points inside a function body, not just at its return.
2. **Cover builtins/intrinsics, not just user-defined functions.** `_maFuncs` (the summary's
   registration table) only covers top-level user functions. Every `Ashes.Text.fromInt`-style
   producer whitelist (Strings, Bytes, BigInt) should instead be a **declared fact on the intrinsic's
   metadata** (extending `BuiltinRegistry`, which already carries per-intrinsic metadata for other
   purposes) — "this intrinsic's result is fresh and uniquely owned" — looked up once, not
   re-derived by parsing the call-site AST's qualified name every time.
3. **Cover closure identity/provenance.** `_functionReturnedClosureLabels`/`_knownFunctionLabelsBySlot`
   (the backward-scan system implicated in the fannkuch-redux leak) should become a field on the
   summary, computed by the same fixpoint that computes `ResultReach`, extended to reason about
   closure identity through curried/forwarding returns — the exact case the current backward scan
   cannot see.
4. **Cover TCO parameters.** `TcoContext`'s ~17 independently-scanned classification fields should
   shrink to reading one ownership decision per parameter off the (now expression-level) summary;
   the all-or-nothing frame veto should be deleted, since a unified per-value model can express
   "this parameter is RC, that one in the same loop is arena" safely (today it cannot, which is
   *why* the veto exists — see the CO-38/PR #299 precedent, which shows exactly this kind of
   accidental mixing being unsafe when done *implicitly*; the goal is to make it safe and *explicit*).

Once ownership is decided this way, **region/arena/stack placement becomes a downstream question**:
"has the ownership analysis proven this value non-escaping / scope-bounded?" — if yes, arena is a
legal, purely-representational optimization; if no, RC. `CanArenaReset`/`GetTcoCopyOutKind` would be
*consulted* only for values the ownership layer has already cleared, not run independently against
every value's raw type as a competing classification.

For the two whole-program gates (`_usesAsync`/`_inCoroutineBody`, `CapabilityGlobalCount`), the
proposal is **not** to remove the underlying restriction (which is real for the task struct and the
one-shot post's captured extent) but to **narrow its granularity**: from "is this program/function
touched by async/capabilities at all" to "does this specific value's live range intersect an actual
suspend point / actual dynamic capability dispatch." The `LivePostsGuard` mechanism (already
correctly scoped to a *temporal* window, if not a *value* window) and `StateMachineTransform`'s
existing per-suspend-point liveness analysis (already computing exactly the save/restore set needed)
are both cited by name in the audits as the natural sources for this narrower signal — they already
exist and already compute close to what's needed, just for a different consumer today.

```mermaid
flowchart TB
    subgraph target["Target architecture"]
        AST2[AST / expression]
        AST2 --> OWN["Unified ownership analysis\n(extended FunctionOwnershipSummary)"]
        OWN --> DECISION["owner / dup sites / drop sites /\nuniqueness / reuse-legality"]
        DECISION --> REP{"Representation optimization"}
        REP -->|proven non-escaping| ARENA2["Region / arena"]
        REP -->|escapes / suspends / shared| RC2["Runtime RC"]
        REP -->|closure, non-escaping| STACK2["Stack"]
        REP -->|proven unique + compatible| REUSE2["In-place reuse"]
    end
```

## 6. Migration plan

Do **not** attempt this as one change. The audits independently converge on a category ordering
driven by complexity and by which categories' fixes are prerequisites for others:

**Phase 0 — prerequisite, no behavior change.** Extend `OwnershipSummary.cs`'s
`FunctionOwnershipSummary` record with the new fields (§5, items 1-4) *without yet routing any
classifier through it* — i.e. compute it in parallel, assert-compare its answer against each existing
classifier's answer in debug builds, and fix any disagreement found before anything depends on the
new analysis. This directly reuses the project's own established practice (soak-testing new
ownership logic against old before cutover) and produces, as a side effect, concrete evidence of how
often the new analysis would have changed behavior versus the old ad hoc classifiers.

**Phase 1 — Strings/Bytes builtin-freshness metadata (Medium complexity, low risk).** Collapse
`IsRuntimeRcStringProducer`/`IsRuntimeRcBytesProducer`/`IsRuntimeRcBigIntProducer` into one
table-driven lookup against `BuiltinRegistry` metadata. Mechanical, narrow blast radius, and proves
the "consult a declared fact instead of re-deriving from AST shape" pattern end-to-end before
touching anything TCO/reuse-adjacent. Bytes' view/owned distinction stays a first-class tag (§7) —
this phase does not touch that.

**Phase 2 — Capability gate narrowing (Medium complexity, concentrated).** Replace
`CapabilityGlobalCount > 0` with a genuinely dynamic-dispatch-scoped signal (distinguish
static-`provide`-only programs, which need no exception at all, from programs with real `handle`
sites). Concentrated in ~15 call sites all in three files; the fix is narrowing a predicate, not
redesigning a mechanism. Do this before touching async narrowing (Phase 7) since the two gates are
currently checked together at every site and separating them first makes each easier to reason about
independently.

**Phase 3 — Closure result/argument provenance unification (Medium complexity) — landed.** Fold
`_functionReturnedClosureLabels`/`_runtimeManagedFunctionResultLabels`/`_knownFunctionLabelsBySlot`
into the extended summary (§5 item 3). **This phase bundles two mechanistically distinct fixes —
keep them distinguished, both here and in any future retelling:**

1. The `FunctionResultProvenance`/closure-forwarding mechanism itself (the phase's originally-scoped
   work). Measured effect on fannkuch-redux N=11 in isolation: small, ~45 GB → ~44 GB (~2%).
   `FunctionResultProvenance`'s "forwards to another function" classification answers "does the
   *whole result* forward," not "is *this constructor argument* fresh when it comes from calling
   another user function" — the latter is a genuinely different, inter-procedural question
   (`IsFreshConstructionArgument`'s domain) needing each callee's own provenance resolved recursively
   through a call cycle (`rotateFirst`/`setAt`/`insertAt`/`nextPerm` all call each other in the
   fannkuch accumulator chain) — real, separate work, adjacent to Phase 6/`GetAdtField` territory,
   correctly declined rather than chased under this phase's scope.
2. A second, narrower fix folded into the same phase: broadening `LowerVar`'s `Borrow`-path
   condition (registering a borrowed alias as "already RC-safe" under the same general condition
   already used for the pre-borrow temp, instead of only the narrow TCO-alias case) — a different
   code path than `FunctionResultProvenance` entirely. **This fix alone reproduces the full
   47 GB → 27.4 GB fannkuch-redux improvement**, verified twice for reproducibility, output unchanged
   (checksum `556355`, `Pfannkuchen(11) = 51`), and confirmed via manual revert-and-rebuild that a
   regression test genuinely depends on it.

fannkuch-redux N=11 after Phase 3: **~27.4 GB**, down from the ~47 GB regression baseline — a real,
substantial, verified improvement, but **not the whole story**: 27.4 GB is still far above the
pre-regression historical baseline (~3.8 MB). **fannkuch-redux's remaining dominant leak driver is
confirmed to be a separate, third gap**: `GetAdtField`/match-extracted-field provenance in
`Lowering.Patterns.cs` (a `match st with | S(perm, count) -> ...`-shaped extraction question, not a
closure-forwarding question) — this remains unfixed and is not part of Phase 3, 4, or 5 as currently
scoped; whoever tackles it next should treat it as its own investigation, likely adjacent to Phase 6,
and should start from Phase 3's own `ASHES_EXPLAIN_OWNERSHIP=all` trace of
`nextPerm`/`rotateFirst`/`setAt`/`insertAt`/`loop` against the real
`challenges/fannkuch-redux/fannkuch-redux.ash` rather than re-deriving the diagnosis from scratch.

**Phase 4 — ADTs and Tuples per-expression freshness — landed.** Added a standalone "top-cell
fresh" query (`Lowering.TopCellFreshness.cs`) — a syntactic sibling of Phase 0's
`ExpressionFreshness`, not folded into its interprocedural fixpoint (see the status note above for
why). `ProducesFreshRuntimeManageableAdt`/`IsFreshRuntimeManageableAdtExpression`/
`IsFreshConstructorTree` now route through it, and its `AnyArmConsistentlyFresh` helper generalizes
the CO-38/PR #299 per-arm reconciliation (every terminal arm building the same parent type must
independently agree, never an OR across disagreeing siblings) to an arbitrary number of sibling
constructors, not just the original bug's binary if/else. Of the audit's 3 tuple-specific
predicates, only `ProducesFreshTuple` was genuinely a duplicated freshness classifier (moved onto
the same shared control-flow walk, `CollectFreshEscapeTerminals`); `CanRuntimeManageFreshTupleExpression`
and `CanRuntimeManageOwnedTupleType` turned out to be type-shape/per-field-producer-acceptance gates
analogous to the 7 orthogonal ADT predicates, not freshness classifiers, and were left alone.
`ProducesFreshTuple` keeps its original existence-check (OR) semantics rather than adopting the
ADT's AND-based reconciliation — see the status note above for why that turned out not to apply to
tuples. Full C# suite (1693/1693), LSP suite (52/52), e2e suite (539/0), and the full `challenges/`
suite (binary-trees N=21, fannkuch-redux N=11, n-body, spectral-norm, mandelbrot, fasta,
reverse-complement, k-nucleotide, 1brc) all confirmed unchanged versus the Phase 3 baseline.

**Phase 5 — Lists — landed.** Added `ProducesFreshRuntimeManageableList`
(`Lowering.TopCellFreshness.cs`), the List sibling of Phase 4's `ProducesFreshRuntimeManageableAdt`/
`ProducesFreshTuple`: it routes `IsFreshListConstructionExpression` through the shared
`CollectFreshEscapeTerminals` walk so a fresh list literal returned from an if/match arm is now
recognized as an escaping runtime-managed result — previously only the whole let/lambda body being
the construction directly was recognized. Initially kept Tuple's existence-check (OR) semantics on
the theory that, unlike an ADT, a list has no sibling-constructor identity for an AND-style
reconciliation to even key on. That theory was checked directly (per a coordinator review) and found
incomplete: it explains why there is no "different constructor of the same type" hazard, but missed a
different mixing hazard that needs no group key at all (a bare-Var passthrough of an existing binding
sharing an escape position with a genuinely fresh sibling arm) — confirmed live by direct IR
inspection (an orphaned `Alloc{RuntimeManaged: true}` with no reachable `RcDrop`), though NOT
confirmed as an actual process-memory leak at the compiled-binary level (see the status note above for
the full account of what was and wasn't verified). Landed as AND (every terminal independently fresh),
closing the bookkeeping disagreement outright regardless of its measured runtime impact — pinned by a
dedicated regression test and an e2e test looped at real scale. Deliberately did NOT widen
`IsFreshListConstructionExpression`'s terminal set (`ListLit` /
`Cons`-chain-to-`ListLit` only, never `Var`/`Call`) when adding the control-flow transparency — this
is the tail-sharing safeguard: it keeps this RC-promotion question syntactically disjoint from the
arena/TCO side's laxer `IsFreshListRebuildExpr` (which answers "safe to whole-clone at a back-edge,"
a different question with a different risk profile), so a threaded/shared-tail list can never be
RC-promoted by this engine regardless of which call site asks — pinned by two adversarial regression
tests (a bare-Var-tail cons and a recursive-call-tail cons, both required to stay arena-managed).
Reading `LowerConsCell` closely (rather than assuming the two reuse-token proofs applied symmetrically
to Lists) found they don't: the RC-managed reuse proof (`TryGetRuntimeManagedReuseScrutinee`) is
gated to `TypeRef.TNamedType` and categorically excludes `TList`, so a list cons cell is never
satisfied through it — only ever through the arena `_linearReuseNames` path. That path's own
bookkeeping had a latent, pre-existing bug: the post-emission `_runtimeManagedResultTemps` marking
keyed off the `runtimeManaged` request flag alone, independent of whether the reuse-token branch
(always an arena `AllocReusing`, `RuntimeManaged: false`, no RC header) or the fresh-`Alloc` branch
(which honors `runtimeManaged`) actually ran — so a reuse-token hit could mark an arena-reused cell
runtime-managed anyway, which would make a later `RcDup`/`RcDrop` read/write a bogus header at that
address. Fixed by keying the bookkeeping directly off which branch emitted the cell; this is the
phase's reuse-token reconciliation for Lists (there is no second proof to unify against once the
`TypeRef.TNamedType` gate is understood — the fix makes the one real proof internally consistent
instead). No `.ash`-level repro reaching the vulnerable state was found within this session's budget
(needs a reuse token and the narrow list-tail-extension binding to coincide); the fix is verified by
full-suite regression (zero behavior change) and by inspection, flagged as such rather than presented
as fully pinned by a dedicated fails-without-the-fix test.

Full C# suite (1698/1698, +5 new `OwnershipTests`), LSP suite (52/52), e2e suite (542/0, +3 new),
and `dotnet format --verify-no-changes` all green, all unchanged versus the Phase 4 baseline except
the new tests. Full `challenges/` suite re-run and compared byte-for-byte against a Phase-4-baseline
binary: 1BRC (`m10000000.txt`, 10M rows: 6.97 GB / ~0.7 s; `m100000000.txt`, 100M rows: 9.05 GB /
~1.4 s — both output byte-identical to baseline, no CO-32-shaped regression, re-confirmed after the
AND-reconciliation fix), fannkuch-redux N=11 (27.44 GB, checksum `556355`, `Pfannkuchen(11) = 51` —
matches the ~27.4 GB Phase 4 baseline, this phase does not move that number as expected since its
residual leak driver is match-extraction provenance, not list-element classification; also
re-confirmed after the fix), n-body (3,000,000 steps, `List(Body)` accumulator, CO-32's original test
case), binary-trees N=21, spectral-norm, mandelbrot, fasta, pidigits, reverse-complement, and
k-nucleotide — every one byte-identical output and unchanged time/RSS versus a baseline binary built
from the pre-Phase-5 tree.

**Phase 6 — TCO loop-carried values (Very large, depends on Phases 3-5) — attempted, not landed.**
Scoped as: collapse the four independent classifiers (A/B/C/D, §1) into one per-parameter ownership
decision; delete the all-or-nothing frame veto; fold pattern-derived-alias tracking (D) into ordinary
dup/drop placement instead of a separate pending-sites table; and, per the project owner's explicit
exit criterion, close fannkuch-redux N=11's residual ~27.4GB leak (versus a ~3.8MB pre-regression
baseline) as a natural consequence of fixing (D). None of this landed. Two independently-scoped fixes
were designed, implemented, and measured; both were confirmed — not merely suspected — to make things
worse, and both were reverted. This section records the investigation in full so whoever picks this
phase up next does not have to re-derive it.

The four classifiers the design catalogued are all still present, largely under the names given in
§1: `TcoContext.RuntimeManagedParamSlots` (`Lowering.Types.cs`, populated by
`LowerLambdaCoreIdentifyRuntimeManagedTcoParams` in `Lowering.cs`), the `GetTcoCopyOutKind`/arena
`TcoBackEdge*` machinery (`Lowering.cs`/`Lowering.Ownership.cs`), `_runtimeManagedResultTemps`
(`Lowering.cs`), and `_runtimeManagedTcoPatternAliases`/`_pendingNestedTcoPatternAliasSites`
(`Lowering.cs`/`Lowering.Patterns.cs`). The all-or-nothing veto is
`LowerLambdaCoreRejectPartialRuntimeManagedTcoFrame` (`Lowering.cs`): it clears every param's
runtime-managed status the moment any one heap-typed param in the same frame fails eligibility.
Confirmed directly (via a temporary env-var-gated instrumentation hook on
`LowerLambdaCoreIdentifyRuntimeManagedTcoParams`, since removed): for `fannkuch-redux`'s `loop`, the
veto never fires — `st` (the only non-copy-type parameter, a `State = S(List(Int), List(Int))`
accumulator) is the *only* candidate and it independently qualifies, so there is nothing else in the
frame for it to conflict with. This means the veto is not fannkuch's problem and deleting it, by
itself, would not have moved the exit-criterion number — a first concrete data point against the
design doc's own working assumption that closing classifier (D) would be sufficient.

*Diagnosis, done first, from real IR rather than re-derivation.* Following the design doc's own
pointer (Phase 3's `ASHES_EXPLAIN_OWNERSHIP=all` trace of `nextPerm`/`rotateFirst`/`setAt`/
`insertAt`/`loop`), two temporary instrumentation hooks were added (env-var gated, both removed
before this write-up): one dumping a `TcoContext`'s resolved `RuntimeManagedParamSlots` by self-name,
one dumping a function's fully lowered (post-`ResolveDeferredTcoResets`) instruction stream by label
or local-variable name. Reading `loop`'s and `nextPerm`'s real IR side by side (not the doc's
paraphrase of them) found the actual mechanism:

1. `nextPerm`'s body is `if r == n then Done else match st with | S(perm, count) -> ... if cr > 0
   then Continue(S(perm2)(count2))(r) else nextPerm(r + 1)(n)(S(perm2)(count2))`. The **recursive**
   branch's `S(perm2)(count2))` — a tail-call argument to `nextPerm` itself — is emitted as
   `AllocAdt { RuntimeManaged: true }`, because `_runtimeRcTcoAdtAllocationRequested` is set while
   lowering a TCO tail-call's own arguments (`LowerCallTcoEvalArg`'s `freshAdt` branch). The
   **terminal** branch's `S(perm2)(count2))` — nextPerm's own escaping *result*, not a call
   argument — is emitted as `AllocAdt { RuntimeManaged: false }` (confirmed by direct IR inspection),
   because the general escaping-result freshness check (`ProducesFreshRuntimeManageableAdt`, called
   from `LowerEscapingResult` for every lambda body) never recognizes it as fresh:
   `IsFreshRuntimeManageableAdtExpressionCore`'s constructor-application gates
   (`CanRuntimeManageCopyAdt`/`CanRuntimeManageGenericCopyAdtConstructorApplication`/
   `CanRuntimeManageFreshHeapChildAdtConstructorApplication`/
   `CanRuntimeManageOwnedChildAdtConstructorApplication`/`CanRuntimeManageRecursiveCopyAdt`) all
   ultimately route a nested named-type field through `CanRuntimeManageAdt`, which requires the
   nested type have *record syntax* (`DeclaringSyntax.FieldNames.Count > 0`) — `State`'s constructor
   is positional (`S(List(Int), List(Int))`), so `CanRuntimeManageAdt(State)` is false, and
   `CanRuntimeManageOwnedChildAdt`'s own multi-constructor gate (`Constructors.Count < 2`) means it
   can't accept `State` either. The *TCO-parameter* eligibility gate
   (`CanRuntimeManageTcoOwnedChildAdt`/`CanRuntimeManageTcoOwnedChildAdtConstructorFields`) already
   handles exactly this shape (a positional single-constructor type whose fields are copy types or
   lists of copy types) — that is how `st` itself qualifies as a TCO parameter in the first place —
   but the general escaping-ADT-freshness classifier never consults it. Two different classifiers,
   answering what should be the same "is this ADT shape ownable" question, disagree about the same
   type — precisely the disease this whole document is about, just crossing between Phase 4's ADT
   classifier and Phase 6's TCO classifier rather than within either one.
2. Because the terminal branch's result is arena, `loop` (nextPerm's caller) — whose own `st`
   parameter *is* runtime-managed — cannot adopt it cheaply. `loop`'s TCO back-edge normalization
   (`TcoBackEdgeNormalizeRuntimeManagedArg`) checks `alreadyRuntimeManaged` (from
   `IsRuntimeManagedResultTemp`, called on the lowered tail-call-argument temp) and, finding it false,
   falls through to `EmitRuntimeManagedTcoDeepCopy`: a full `CopyOutArena` + per-field `CopyOutList`
   deep copy of the whole `State`, allocating a **second**, independent copy of the entire
   permutation/counter list pair, every single outer loop iteration (confirmed live in the resolved
   IR: `CopyOutArena { StaticSizeBytes = 24, RuntimeManaged: true }` immediately followed by two
   `CopyOutList` calls, right where the back-edge installs the new `st`). This — not classifier (D)'s
   pattern-alias table — is the dominant, measured driver of the ~27.4GB residual: one O(size)
   promotion copy per outer loop iteration (up to 11! ≈ 39.9M iterations for N=11), permanently
   arena→RC-converting a value that, if the terminal branch built it RC in the first place, would
   need no conversion at all.

*Fix attempt 1 (construction side only) — made memory worse, reverted.* Extended
`IsFreshRuntimeManageableAdtExpressionCore` to also accept
`CanRuntimeManageTcoOwnedChildAdtConstructorApplication`, and extended
`CanRuntimeManageOwnedChildAdt`'s and `CanRuntimeManageFreshOwnedChildExpression`'s per-field
type/expression checks to recognize a nested positional single-constructor ADT (via
`CanRuntimeManageTcoOwnedChildAdt`) and a nested fresh constructor application of it (via a recursive
`IsFreshRuntimeManageableAdtExpression` call), respectively. Verified by direct IR inspection that
this correctly flips `nextPerm`'s terminal-branch `S(perm2)(count2)`/`Continue(...)` construction to
`RuntimeManaged: true`, matching its recursive-branch sibling. **Measured effect on fannkuch-redux
N=11: RSS *increased*, 27,440,640 KB → 28,999,936 KB** (checksum and `Pfannkuchen(11)` output
unchanged — still correct, just bigger). Root cause of the regression, found by re-inspecting the
resolved IR after the change: making the construction RC without also fixing *consumption* is net
negative. `loop`'s own back-edge still does not recognize the (now genuinely RC) `Step`/`Continue`
result as already-fresh (`IsRuntimeManagedResultTemp` only recognizes specific instruction targets —
`AllocAdt`, `ConcatStr`, etc. — reached by a backward scan; a `CallClosure` result, which is what
`nextPerm(...)`'s return value is at the use site inside `loop`, is not among them), so `loop` still
performs its own full defensive deep copy exactly as before — now copying an *already-RC* source
instead of an arena one. Worse, the `Step` wrapper `nextPerm` returns is never registered as an owned
value at all: `TrackRuntimeManagedMatchScrutinee` (the mechanism that would normally arrange for a
matched scrutinee's drop) only fires for a `Var`-bound owned scrutinee or an
`IsRuntimeManagedResultTemp`-recognized temp — neither applies to a bare `Call` expression scrutinee
(`match nextPerm(...) with | Continue(st2, r2) -> ...`) — so the now-RC wrapper cell is allocated and
then never dropped: a brand new leak that did not exist when the same value was arena (arena needs no
drop). This is exactly the interprocedural, "does an ordinary call's result — not just a TCO tail-call
argument — need RC promotion, and can the caller adopt it without re-copying" question Phase 3's own
write-up flagged and explicitly declined to chase ("a genuinely different, inter-procedural
question... needing each callee's own provenance resolved recursively through a call cycle
(`rotateFirst`/`setAt`/`insertAt`/`nextPerm` all call each other)"). Reverted in full.

*Fix attempt 2 (classifier D itself) — closes a real, independently-confirmed gap, but a narrower one
than fannkuch's driver, and a broader version of it regresses an existing regression test.* Separately
from the fannkuch investigation, direct instrumentation of `TrackRuntimeManagedTcoListPatternAliases`
(the eager half of classifier D) found it can **never fire** for an ordinary, unannotated TCO
accumulator: the check it makes — `_tcoCtx.RuntimeManagedParamSlots.Contains(parent.Slot)` — reads a
classification computed once at loop entry, *before* the body (including the very match statement
being lowered) has run; a parameter's eligibility for an unannotated accumulator is only known after
the whole body is lowered and `LowerLambdaCoreRefreshRuntimeManagedTcoParams` has run — which is
*after* this check's only opportunity to fire. Confirmed live: for both a synthetic ADT-scrutinee
test and (by inspection) fannkuch's own `S(perm, count)` match, `RuntimeManagedParamSlots` is empty at
exactly this checkpoint. This falsifies `ResolvePendingNestedTcoPatternAliasSites`'s own documented
assumption that "direct" bindings (extracted straight off a declared TCO parameter, as opposed to a
multi-level chain) are already protected by the eager pass and can safely be excluded from the
retroactive one — for an unannotated accumulator, *neither* pass protects a direct binding today.
Widening `TrackRuntimeManagedTcoListPatternAliases`'s scrutinee-type guard from `TList`-only to also
accept `TNamedType`/`TTuple` (closing the literal "ADT scrutinees are invisible here" gap named in the
design doc) is correct but inert on its own, since the eager pass this widens essentially never fires
for realistic unannotated loops regardless of scrutinee type. The change that actually matters is in
`ResolvePendingNestedTcoPatternAliasSites`: replacing its "exclude any direct binding" filter with
"exclude only a binding the eager pass actually registered" correctly retroactively protects a direct
binding the eager pass missed. A new adversarial test
(`Adt_scrutinee_field_reused_in_tco_back_edge_gets_protective_dup`) confirmed both directions: it
fails without this change (no `RcDup` anywhere in the function) and passes with it. But the *broader*
form of this fix regresses `NestedTcoPatternAliasTests.Copy_typed_tuple_field_is_not_protected_as_
runtime_managed` — the existing PR #298 regression test — because a plain list-cons-level direct
binding that simply becomes the *same parameter's own next value* (e.g. `rest` flowing unchanged into
the tail call that replaces `tbl`) is already correctly handled by the ordinary per-parameter back-edge
argument-threading machinery (classifier B), independent of the pattern-alias table entirely; blanket
including every direct binding as a retroactive-protection candidate now also protects bindings that
never needed it (e.g. `pair`, consumed immediately by a nested match and never independently escaping)
whenever eager didn't happen to register them, producing extra, unwanted protective dups the existing
test correctly caught. Distinguishing "this direct binding independently escapes and needs the
pattern-alias table's protection" from "this direct binding merely becomes the parameter's own next
value and is already handled by the ordinary argument-threading path" is exactly the liveness question
the design doc's item 4 (§5) describes as needing the extended `FunctionOwnershipSummary` to answer
properly, rather than a further ad hoc condition bolted onto the pending-sites table. Reverted in
full alongside its test.

**Net result: no code change landed for this phase.** The tree at the end of this investigation is
byte-for-byte identical to its start (verified: `git status`/`git diff` clean against the merged
`main` tip). fannkuch-redux N=11 is unchanged at **27,440,640 KB peak RSS** (checksum `556355`,
`Pfannkuchen(11) = 51`) — the exit criterion was not met. Per the explicit instruction that accompanied
this phase ("if it turns out to need a fifth, genuinely separate mechanism... STOP and report that
discrepancy clearly rather than either silently shipping without meeting the exit criterion, or
forcing an unsound fix just to move the number"), this is reported as exactly that discrepancy rather
than rounded up. What is now confirmed, with IR-level evidence rather than inference from source: (a)
the all-or-nothing veto is not fannkuch's problem and can likely be removed later without affecting
this benchmark either way, though its removal was not attempted here given the scale of the four-
classifier collapse this would need to be validated alongside; (b) fannkuch's residual leak is a
construction/consumption pair spanning Phase 4's ADT-freshness classifier and an interprocedural
call-result-freshness question Phase 3 already flagged and declined — not primarily a TCO
parameter-classification question, so it sits only partly inside this phase's own scope as titled; (c)
classifier (D)'s pattern-alias table has a real, independently-reproducible protection gap for direct
bindings off an unannotated TCO accumulator (of any scrutinee type, not just ADTs), which is exactly
"fold pattern-derived-alias tracking into ordinary dup/drop placement" territory, but closing it
correctly needs the same liveness reasoning the extended `FunctionOwnershipSummary` is meant to
provide, not a further special case in the existing ad hoc table — attempting the latter produces a
choice between "too narrow to help" and "too broad, regressing an existing UAF-history regression
test," with no safe middle ground found in the time available. Whoever picks this up next should
likely sequence it as: first extend `FunctionOwnershipSummary`/`ExpressionFreshness` to answer "is a
specific call's result fresh, resolved recursively through the callee's own body" (the item Phase 3
explicitly deferred), since that is a prerequisite for closing fannkuch's actual leak soundly; only
then attempt the TCO-specific classifier collapse and veto removal this phase was titled for, now able
to route classifier (D) through the same per-value liveness answer instead of patching the pending-
sites table's exclusion rule directly.

**Phase 6 resolution — interprocedural call-result freshness landed, fannkuch-redux ~27.4GB → ~1.56GB.**
A follow-up session picked up exactly where the investigation above left off, closing the
"interprocedural call-result freshness" gap Phase 3 originally flagged and declined
("`IsFreshConstructionArgument`'s domain... needing each callee's own provenance resolved recursively
through a call cycle... real, separate work, adjacent to Phase 6"). This did **not** attempt the
four-classifier TCO collapse or veto removal Phase 6 was titled for — those remain unstarted (see
"What's still open" below) — it closed specifically the construction/consumption pair the investigation
above diagnosed as fannkuch's dominant leak driver.

*Re-verifying the prior diagnosis.* The investigation's IR-level diagnosis (construction-side gate
rejecting `nextPerm`'s `Continue(S(perm2)(count2))(r)` terminal branch because `CanRuntimeManageAdt`
requires record syntax) was re-confirmed unchanged against current `main` — names and line numbers had
not drifted. The construction-side fix that investigation already designed and IR-verified
(`CanRuntimeManageOwnedChildAdt`'s and `CanRuntimeManageFreshOwnedChildExpression`'s per-field
dispatch extended to accept `CanRuntimeManageTcoOwnedChildAdt`'s looser positional-single-constructor
test alongside `CanRuntimeManageAdt`'s record-only one) was re-implemented from this description rather
than recovered from any surviving diff (fix attempt 1 was fully reverted, nothing to restore). A cycle
guard (`HashSet<TypeSymbol>? path`) was added to `CanRuntimeManageOwnedChildAdt`/`CanRuntimeManageTcoOwnedChildAdt`
that the original design did not need: the new mutual delegation between the two functions (a field's
type can now route through either one) opens a cross-function recursion path a pair of mutually-
referential heterogeneous ADTs could loop through forever, which neither function's original,
independent recursion needed to guard against.

*The construction-side fix alone was insufficient — as predicted.* Applying only the construction-side
piece and re-measuring reproduced fix attempt 1's finding almost exactly: RSS *increased* (this
session measured ~30.6GB before discovering a build had silently failed and was serving a stale
binary — see the gotcha below — the real construction-only number was not independently re-measured a
second time before the consumption-side fix landed alongside it, since the combined-fix measurement
made further isolation unnecessary). This re-confirms the investigation's core finding: construction
and consumption must land together.

*The consumption side had a deeper blocker than the investigation identified.* The investigation's
plan was to extend `IsFreshConstructionArgument`/`ClassifyExpressionProvenance` (the AST-only
`FunctionResultProvenance` classifier) to resolve nested constructor arguments through `letBindings`
and recognize forwarding calls to other registered functions. Direct measurement of
`FunctionOwnershipSummary.GetOwnershipSummary("nextPerm").ResultProvenance` (via
`ASHES_EXPLAIN_OWNERSHIP=all`) showed `rc-eligible:true` had actually resolved correctly on the very
first attempt — reusing `IsFreshRuntimeManageableAdtExpressionCore` (see "safe reuse over a parallel
rule" below) as a fallback inside `IsDirectRcConstruction`, rather than the narrower per-argument
letBindings/forwarding-call rule the investigation sketched, was sufficient. `TryResolveKnownFunctionResultOwnership`
was *still* returning `false` at `nextPerm`'s call sites regardless, `argType=Step runtimeManagedResult=False`
confirmed by direct instrumentation of `LowerCallGeneral`. Tracing `TryResolveKnownFunctionResultOwnership`'s
own gate conditions one at a time (temporary per-condition logging, since removed) isolated the failure
to `TryResolveKnownFunctionLabel(rootExpr, ...)` itself failing — a layer beneath `ResultProvenance`
entirely, and beneath anything Phase 3 or the Phase 6 investigation had examined.

The actual root cause: `LowerLetRecursiveFinalizeValue` (the finalizer that runs for *every*
self-recursive `let recursive` binding — i.e. nearly every function in fannkuch-redux, and in most
real Ashes programs) only ever populates `_functionNameByLabel` (the reverse label → function-name
lookup `TryResolveKnownFunctionResultOwnership` needs to find a callee's `FunctionOwnershipSummary`)
when that function's own closure has a **fully empty capture environment**. Any `let recursive`
function whose body calls even one sibling top-level helper — the ordinary shape, since a
non-trivial recursive helper calling nothing else is the exception — ends up with a *non-empty*
environment (calling another top-level binding from inside a nested lambda requires capturing it,
regardless of whether that binding is itself a "known function"), and therefore silently never
gets a `_functionNameByLabel` entry. `nextPerm` itself is exactly this shape (it calls `rotateFirst`,
`getAt`, `setAt`). The parallel, non-recursive path (`LowerLetRegisterKnownFunctionIdentity`) already
had a fallback branch for precisely this case ("a capturing top-level let is not callable as a global
label... but the closure in this exact local slot has a statically known code label" — registers
`_knownFunctionLabelsBySlot`/`_functionNameByLabel` without the empty-env-only `_topLevelFunctionRefs`
entry); `LowerLetRecursiveFinalizeValue` never got the equivalent. This one gap silently defeated
`TryResolveKnownFunctionResultOwnership` — and therefore all of Phase 3's interprocedural machinery —
for essentially every recursive function in the compiler, not merely `nextPerm`; it is squarely a
"consumption side" wiring gap (the mechanism's own doc comment at `_functionNameByLabel`'s declaration
already documented the intended invariant this violated: "Populated wherever `_topLevelFunctionRefs`
(or an empty-env-only equivalent...) is written"), not a new mechanism, and is fixed the same way —
an `else if` branch in `LowerLetRecursiveFinalizeValue` mirroring the non-recursive fallback exactly
(`_knownFunctionLabelsBySlot[slot]`/`_functionNameByLabel[label]`, no `_topLevelFunctionRefs` entry,
since a non-empty-env function still needs its captured environment to be called correctly).

*Safe reuse over a parallel rule.* Rather than implement the letBindings-substitution/forwarding-call
rule the investigation sketched for `IsFreshConstructionArgument` (a second, independently-derived
classifier answering the same question the construction side already answers), `IsDirectRcConstruction`
was extended to fall back to `IsFreshRuntimeManageableAdtExpressionCore` — the *exact* function the
construction side's own `LowerEscapingResult`/`ProducesFreshRuntimeManageableAdt` path consults — when
its own narrower, argument-shape-only check fails. Both call sites (`Lowering.cs`'s escaping-result
path, now also `Lowering.OwnershipProvenance.cs`'s pre-lowering AST pass) now consult literally the
same function, so a future change to the construction-side classifier can never leave the
interprocedural consumer silently out of sync with it — the exact "two classifiers, same question,
different code paths, can disagree" disease this whole document is about, closed by construction for
this specific pair rather than reproduced a third time. This was checked for a safety regression the
investigation would have flagged: `IsFreshRuntimeManageableAdtExpressionCore`'s own per-field checks
(`CanRuntimeManageFreshOwnedChildExpression`'s `TStr`/`TBytes`/`TBigInt`/`TNamedType`/`TTuple` cases
all require the argument to independently prove itself a producer, a `RecordLit`, or a nested fresh
constructor tree — never a bare `Var`) reproduce the `reuse_result_alias_move_elision.ash`
(`wrap v x = Node(x)(v)(Leaf)`) regression's protection exactly: a consumed-parameter argument to a
non-list, non-scalar field is still never accepted. The one field shape the fallback treats
permissively regardless of the argument's own freshness is `TList` of a copy-type element
(`CanRuntimeManageFreshOwnedChildExpression`'s existing, pre-dating-this-fix `TList` case already
accepted a bare `Var`/`Call` there) — confirmed safe by reading `LowerRuntimeManagedConstructorArgument`
(`Lowering.Symbols.cs`): it unconditionally emits a defensive `CopyOutList{RuntimeManaged: true}`
promotion for exactly this field shape whenever the argument's own temp is not already recognized
runtime-managed, so the field's *actual* stored value is always an independent RC copy regardless of
what expression produced it — aliasing is severed by a real copy at construction time, not merely
assumed absent.

*Two new adversarial regression tests* (`OwnershipProvenanceTests.cs`) pin this directly rather than
only by argument: `Positional_single_constructor_field_nested_in_a_multi_constructor_parent_is_rc_eligible`
(the exact `Step`/`State` shape; confirmed to fail without the `IsDirectRcConstruction` fallback by
temporarily disabling it and re-running) and
`Bare_variable_directly_as_a_nested_adt_typed_field_is_never_treated_as_fresh` (a `Continue(st)(r)`
construction handing the caller's own `st` parameter — unchanged — into the nested ADT-typed field;
confirms `IsFreshTcoOwnedChildAdtConstructorApplication` requires an actual constructor application,
never a bare `Var`, closing off the one hazard the new nested-ADT-field path could have introduced
that the pre-existing `TList` permissiveness argument above does not cover).

*Measured result.* fannkuch-redux N=11: peak RSS **27,440,640 KB → ~1,557,000 KB** (measured
1,557,248 / 1,558,016 / 1,556,736 KB across three separate compiles — noise-level variance, not a
regression signal), a **17.6x reduction**. Checksum `556355` and `Pfannkuchen(11) = 51` unchanged
(byte-identical stdout against a baseline binary built from unmodified `main`, `cmp -s` verified, not
just eyeballed). Full validation: C# suite 1703/1703 (+2 new tests), LSP suite 52/52, e2e suite
542/0, `dotnet format --verify-no-changes` clean. Full `challenges/` suite re-run against a baseline
compiler built from `main`'s actual merged tip (a separate clone, not just "no local diff"): binary-trees
N=21, spectral-norm, mandelbrot, n-body, pidigits, fasta, k-nucleotide, regex-redux,
reverse-complement, and 1BRC (`m10000000.txt`/`m100000000.txt`) all byte-identical output and
unchanged time/RSS versus the freshly-built `main` baseline (1BRC: 10M rows ~6,970,700 KB peak RSS
in both baseline and this fix, 100M rows ~9,048,600 KB in both — no CO-32-shaped regression) —
fannkuch-redux is the only benchmark this phase moves, exactly as expected.

*A tooling gotcha worth recording*: a Meziantou analyzer rule (`MA0051`, method-length) silently
failed the build partway through this session's debugging when a temporary instrumentation line
pushed `LowerCallGeneral` one line over its 60-line cap — `dotnet run --project src/Ashes.Cli`
happily executed a **stale**, previously-built binary from before that edit with no visible warning,
producing a `call-rc` debug log with zero matching output and nearly derailing the diagnosis into
"why does this call never reach `LowerCallGeneral` at all." Always confirm `dotnet build` itself
succeeds (not just that `dotnet run` produced *a* binary) before trusting instrumentation output that
contradicts a hypothesis.

**What's still open after this fix**: fannkuch-redux N=11 at ~1.56GB is still roughly 400x the
~3.8MB pre-regression historical baseline — closing that residual is scoped and characterized here
but was **not** attempted, to avoid delaying this landing:
- The residual scales *linearly* with `N!` (measured: N=8 → 1,280 KB, N=9 → 13,824 KB, N=10 →
  140,800 KB, N=11 → ~1,557,000 KB — each ratio matches `N!/(N-1)! = N` almost exactly, converging to
  ~39.9 bytes leaked per outer-loop iteration), confirming a genuine small residual per-iteration
  leak, not merely a constant-size baseline gap.
- IR inspection (`loop`'s lowered instruction stream) found no `RcDrop` anywhere targeting a `Step`
  (the `Done`/`Continue` wrapper type) — only `List` and `State` typed drops appear. 40 bytes is
  exactly `Step`'s own cell size (1 tag word + 2 fields = 24 bytes payload + 16-byte RC header),
  matching the measured per-iteration leak almost exactly.
- Root cause (diagnosed, not yet fixed): `TrackRuntimeManagedMatchScrutinee`'s `"$match_rc_{temp}"`
  owner-tracking mechanism (`Lowering.Patterns.cs`) registers the match scrutinee (the `Continue(...)`
  wrapper cell itself) as one `OwnershipInfo`, then **aliases every pattern-bound field name to that
  same owner object** (`_ownershipAliases[bindingName] = ownerName`) rather than giving the wrapper its
  own independent tracking. This conflates two different relationships: "same allocation, different
  name" (correct for `let y = x`, where dropping `y` and dropping `x` must agree because they are
  literally the same cell) versus "a field extracted *from* a cell" (`st2`/`r2` extracted from
  `Continue(st2, r2)` are a *different* allocation than the wrapper — extracting them does not make
  them the same physical memory). When `st2` is later moved into the TCO back-edge tail call, the
  *shared* `OwnershipInfo` gets marked `Moved`, and the wrapper's own independent drop — which should
  still fire once its fields have been read out, regardless of what happens to those fields afterward
  — is skipped because the bookkeeping believes the (single, shared) tracked value has already been
  disposed of.
- This is a **leak, not a use-after-free or double-free**: the extracted fields themselves are
  correctly owned and dropped (confirmed: `RcDrop { TypeName = State, RuntimeManaged = True }`
  appears exactly where expected); only the emptied-out wrapper shell's own tiny header allocation is
  never reclaimed. Given the explicit safety discipline for this whole area ("a missed optimization is
  acceptable, a false positive is not") and that `Lowering.Patterns.cs`'s pattern-alias machinery is
  the single most historically dangerous subsystem in this document (CO-8, CO-9, CO-28/29/32/34/35,
  PR #298 all trace back to it), a real fix needs its own dedicated design and test pass — separating
  "same-cell alias" from "field-extracted-from" as genuinely distinct relationships in the ownership
  model, likely by giving the wrapper an independent, always-fires drop instead of only a shared alias
  target — rather than a quick patch under time pressure. Whoever picks this up next should start from
  `TrackRuntimeManagedMatchScrutinee`'s own alias-registration loop (`Lowering.Patterns.cs`) and the
  fannkuch repro (`challenges/fannkuch-redux/fannkuch-redux.ash`, scaling behavior confirmed at N=8
  through N=11 above) rather than re-deriving the diagnosis from scratch.
- The four-classifier collapse and all-or-nothing veto removal Phase 6 was originally titled for
  (§6's own scope) are unaffected by any of the above and remain fully unstarted; the investigation's
  own finding that the veto does not fire for fannkuch specifically still holds (unverified against
  the code changes in this fix, since neither touches TCO parameter classification directly).

**Phase 6 resolution, part 2 — match-scrutinee wrapper drop landed, fannkuch-redux ~1.56GB → 256KB
(constant, no residual scaling).** A follow-up session closed exactly the gap the account above
diagnosed and deliberately left open.

*Re-verifying the prior diagnosis.* The diagnosis above was confirmed unchanged against current
code, with one refinement: `TrackOwnedBindingsInPattern` runs immediately before the aliasing loop
and independently registers its own `OwnershipInfo` for every pattern-bound field name (with
`RuntimeManaged` hardcoded `false`) before the aliasing loop overwrites how that name is *looked up*
(never the scope-dictionary entry itself, since the alias lives in a separate table keyed by name).
That placeholder entry is permanently unreachable once the alias exists — `LookupOwnedValue`
resolves the alias first — and its own eventual "drop" compiles to `IrInst.RcDrop { RuntimeManaged =
false }`, which `LlvmCodegen` turns into a no-op (`IrInst.RcDrop => false` when not runtime-managed).
So the placeholder is genuinely dead weight, not a second live drop path; it does not change the
diagnosis, only confirms there was no hidden double-accounting to worry about while fixing it.

*The fix.* `TrackRuntimeManagedMatchScrutinee` (`Lowering.Patterns.cs`) now distinguishes "same
allocation, different name" from "a field extracted from a cell" instead of aliasing both to the
wrapper's own `OwnershipInfo`. For a freshly constructed scrutinee only (`matchValue is not
Expr.Var` — the exact fannkuch shape; a scrutinee that is itself an existing bound variable is
untouched, see below) whose arm pattern is a single top-level `Pattern.Constructor` with every field
position bound by a plain name (never a nested sub-pattern, never `_`): each such field, if its own
type is not arena-resettable, gets its own independent `OwnershipInfo` (`RuntimeManaged: true`, the
field's own type and slot) via `TrackOwnedValue` — overwriting `TrackOwnedBindingsInPattern`'s dead
placeholder with a live one — instead of an alias entry. The wrapper's own `OwnershipInfo` records
the matched constructor and the set of field indices that were independently extracted
(`OwnershipInfo.ExcludedDropFieldIndices`, new). `EmitKnownConstructorRuntimeManagedAdtDrop` gained an
optional `excludedFieldIndices` parameter (default `null`, so every pre-existing call site is
byte-for-byte unaffected): it now filters the constructor's fields to those *not* excluded before
deciding whether there is anything left to recurse into, and always still emits the final
unconditional `RcDrop` of the value's own header regardless. Net effect: the wrapper's own header and
each independently extracted field are now released by two entirely separate ownership records, each
following its own ordinary move/drop lifecycle — moving a field into a self-recursive tail call
(marking only its own record `Moved`) can no longer suppress the wrapper's own, unrelated header
release, and a field that is never moved is dropped once by its own record with the wrapper dropping
its own header once alongside it, matching the ordinary (non-buggy) case for any other tracked value.

*Why this is safe.* The construction-side and consumption-side invariants that made the wrapper's
own recursive drop dangerous to touch carelessly are both preserved by construction, not by
argument: (1) a field position matched by anything other than a plain top-level name (a nested
constructor/tuple/cons pattern, or the existing `TrackRuntimeManagedTcoListPatternAliases`/
`RecordPendingNestedTcoPatternAliasCandidates` nested-TCO-alias machinery PR #298 hardened) is never
added to the exclusion set and stays on the pre-existing aliasing path, completely unchanged; (2) a
resource-typed field (its own `CleanupResource`/moved-or-closed diagnostic lifecycle, orthogonal to
RC drop bookkeeping) is explicitly excluded from independent tracking and also stays on the
pre-existing path; (3) the "matchValue is an existing bound variable" branch of
`TrackRuntimeManagedMatchScrutinee` — where the reuse-token mechanism
(`GetMatchReuseScrutinee`/`TryGetRuntimeManagedReuseScrutinee`/`PublishTagSwitchArmReuseToken`) and
the nested-TCO-alias fix-up both live — is entirely untouched; this fix only ever changes behavior
for a freshly constructed scrutinee, which by construction is referenced nowhere else and cannot be a
reuse-token candidate. The wrapper's own recursive drop, when it still has any non-excluded
runtime-managed field left (the common no-fix-applies case, or a field position this fix does not
claim), is byte-for-byte the pre-existing code path; when every runtime-managed field was
independently extracted, the `RcIsUnique`-gated child walk simply finds nothing left to do and the
function degenerates to its own pre-existing "no child fields" fast path (unconditional final
`RcDrop`, no walk at all) — the exact behavior an ordinary tracked value with no runtime-managed
fields already gets today.

*Adversarial regression suite* (`MatchScrutineeWrapperDropTests.cs`, five tests; two are
count-precise IR assertions, confirmed to fail without the fix by reverting it locally and
re-running before trusting them): the exact fannkuch shape (field forwarded into a self-recursive
tail call — wrapper drop count `>= 1`, was `0` before the fix); an ordinary non-tail-call match on a
fresh wrapper with the extracted field read once and never forwarded (wrapper drop count exactly
`1`, field's own drop count exactly `1` — not zero, not double); the extracted field used twice in
the arm body; a nested match directly on the extracted field (the untouched "existing variable"
branch); and a field position matched by a nested sub-pattern rather than a plain name (confirms the
gate correctly leaves it on the old path). An end-to-end counterpart
(`tests/runtime_rc_tco_match_scrutinee_wrapper_drop.ash`, a 100,000-iteration version of the exact
shape) and a second one exercising the ordinary/twice-used/nested-match adversarial cases
(`tests/runtime_rc_match_scrutinee_wrapper_drop_ordinary.ash`) both pin correct output end to end.

*Measured result.* fannkuch-redux, before -> after this fix (both built from the same tip, `main`
plus the Phase 6 interprocedural-freshness fix above; times are single-run wall clock on a loaded
32-thread desktop, not isolated, so treat time deltas as indicative rather than precise — RSS is the
load-bearing number here and is unaffected by CPU contention):

| N | RSS before | RSS after | Time before | Time after |
|---|---|---|---|---|
| 8 | 1,280 KB | 256 KB | 0.02s | 0.02s |
| 9 | 14,080 KB | 256 KB | 0.24s | 0.24s |
| 10 | 141,312 KB | 256 KB | 2.87s | 2.80s |
| 11 | 1,556,480 KB | 256 KB | 36.18s | 35.98s |

RSS at every N is now 256 KB — the same floor as N=8, i.e. genuinely constant, not merely reduced;
the residual per-iteration leak this fix targeted is fully closed, not partially. Checksum `556355`
and `Pfannkuchen(11) = 51` unchanged (byte-identical stdout, `cmp` verified, not just eyeballed).

Full validation: C# suite 1708/1708 (+5 new), LSP suite 52/52, e2e suite 544/0 (+2 new),
`dotnet format --verify-no-changes` clean. Full `challenges/` suite re-run against a baseline
compiler built from this same tip with the fix reverted (`git stash` of the three touched
`Ashes.Semantics` files, not a separate clone, since the fix is a pure source change with no build
artifacts to go stale): binary-trees N=21 (196,096 KB vs 196,352 KB baseline, noise-level, byte-
identical output — CO-38 sentinel unaffected), spectral-norm N=5,500, mandelbrot N=4,000, fasta
N=5,000,000, pidigits N=10,000, n-body N=5,000,000, k-nucleotide and reverse-complement (fasta
N=1,000,000 input), regex-redux (fasta N=5,000,000 input), and 1BRC (10M and 100M rows — 9,048,684 KB
in both at 100M, exactly equal, no CO-32-shaped regression) all byte-identical output and
unchanged time/RSS versus the reverted-fix baseline — fannkuch-redux is the only benchmark this fix
moves, exactly as expected.

**What's still open after this fix**: the four-classifier TCO collapse and all-or-nothing veto
removal Phase 6 was originally titled for (§6's own scope) remain fully unstarted and are unaffected
by either part of this resolution. The "matchValue is an existing bound variable" branch of
`TrackRuntimeManagedMatchScrutinee` was deliberately left untouched by this fix (see "why this is
safe" above) — whether it has an analogous wrapper-drop gap of its own was not investigated, since
that branch's owner is the pre-existing variable's own binding (already tracked and dropped by its
own enclosing scope regardless of this match), not a synthetic one this fix's mechanism creates; if
one exists there it is a materially different, smaller-blast-radius shape than the one fixed here and
was not reproduced or measured.

**Phase 6, third attempt — the four-classifier collapse and veto removal, re-attempted and fully
reverted (safety confirmed, profitability not).** A follow-up session picked up exactly the piece
both prior "Phase 6 resolution" sessions explicitly left untouched: collapsing classifiers (A)-(D)
into one per-parameter decision and deleting `LowerLambdaCoreRejectPartialRuntimeManagedTcoFrame`
(the all-or-nothing veto). The classifier catalogue was re-verified against current `main` first and
found unchanged from §1/the original Phase 6 section's own account: (A)
`TcoContext.RuntimeManagedParamSlots`, populated by `LowerLambdaCoreIdentifyRuntimeManagedTcoParams`;
(B) `GetTcoCopyOutKind`/arena `TcoBackEdge*`; (C) `_runtimeManagedResultTemps`; (D)
`_runtimeManagedTcoPatternAliases`/`_pendingNestedTcoPatternAliasSites`; the veto itself duplicated at
two call sites — `LowerLambdaCoreRejectPartialRuntimeManagedTcoFrame` (loop-entry time, unresolved
types) and an identical inline copy of the same "does every param qualify or is it exempt" check
inside `LowerCallTcoPromoteResolvedRuntimeParams` (resolved-type time, re-run at every back-edge call
site) — both funnelling into the same `ClearRuntimeManagedTcoParams` helper.

*The veto fires on real, currently-shipped code, not just synthetic cases.* Before touching anything,
a synthetic adversarial program was built and run against an instrumented build (a temporary env-var-
gated `Console.Error.WriteLine`, since removed) to settle the original investigation's own open
question ("does the veto ever fire for a real program, or only in principle"): a TCO loop threading a
`Str` accumulator (unconditionally RC-eligible by type, per classifier A) alongside a closure
accumulator that is a fresh `given`-lambda literal on some tail self-calls and threaded unchanged on
others (so `FreshClosureParams`'s "always a fresh lambda at every self-call" aggregate never holds for
it). Confirmed live: the veto fires and wipes the `Str` accumulator's otherwise-automatic eligibility
down to nothing, purely because of the co-resident closure's own, unrelated failure. The same
instrumentation was then run against two actual `challenges/` programs and found the veto fires there
too: `challenges/1brc/brc.ash`'s `mergeEntries entries acc` (`entries: List((Str, Int, Int, Int,
Int))`, eligible via the `ConsumedListTailParams` branch — a tail walked one cons cell at a time,
never rebuilt fresh — blocked because its sibling `acc: Trie` is a self-recursive, multi-constructor
ADT that can never be RC-eligible, the same structural shape as binary-trees' own `Tree`/CO-38
precedent) and `challenges/reverse-complement/reverse-complement.ash`'s `emit chars col buf` (`buf:
Str`, unconditionally eligible, blocked because its sibling `chars: List(Str)` fails every exemption:
heap, not arena-resettable, not loop-invariant, and its own consumed-tail shape doesn't qualify for
the list exemption either since a `Str` list element is never arena-resettable and the appended
character is not merely inspected). This settles, with direct evidence rather than inference, that the
veto is not a historical accident nobody's program actually reaches — it is live, load-bearing
behavior in the one nontrivial real-world Ashes program this repository ships as a benchmark.

*Per-parameter mixing across unrelated types is safe — proven, not assumed.* Both veto call sites were
deleted (a pure deletion; every one of the roughly fifty downstream call sites the original
investigation catalogued already keys off `tco.RuntimeManagedParamSlots.Contains(slot)` per slot, never
off a frame-wide boolean, so nothing downstream needed to change to support mixing once the blunt
clear was gone). Full validation with the veto removed: C# suite 1710/1710 (+2 new), LSP suite 52/52,
e2e suite 546/0 (+2 new), `dotnet format --verify-no-changes` clean. The Str/closure adversarial case
above now promotes the `Str` accumulator independently of the closure sibling, confirmed at the IR
level (an `RcDrop{TypeName:"String", RuntimeManaged:true}` appears where none did before) and end to
end at 300,000 and 2,000,000 iterations with correct output. The deeper, directly-adversarial question
the project's own established practice demands ("does per-parameter mixing need a group-level
agreement the way ADTs did, or can each parameter really be judged independently, checked directly
rather than assumed by analogy") was tested with two same-typed `List(Int)` accumulators that trade
values across each other's slots every third iteration (`swapLoop(n-1)(accB)(accA)`) alongside a third
arm that grows each side independently — checked against an independent Python simulation's checksum
(`15000100000` at N=300,000) for a ground truth the compiler's own correctness can't be assumed to
provide. Finding: the swap can never produce a mismatched representation, and it needs no added
reconciliation to prevent one, because the existing per-name candidacy aggregation
(`CollectTcoParamsMatchingArguments`, backing `FreshRebuiltListParams`/`AffineConsListParams`/
`ConsumedListTailParams`/`FreshClosureParams`) already removes a parameter from its own candidate set
the moment ANY source-level self-call passes something other than a qualifying construction into that
parameter's own slot — a swap, by passing a bare variable into the other side's slot, disqualifies
BOTH swap partners simultaneously, by construction, not by an added policy. This is a genuinely
different answer than the ADT/List categories got when this same question was asked of them (ADTs
needed strict AND-based sibling reconciliation across `if`/`match` arms; Lists needed the same for a
bare-Var-vs-fresh-arm hazard nothing initially suspected) — for TCO parameters specifically, the
per-slot aggregation that already existed for an unrelated reason (deciding whether a single
parameter's own back-edge argument looks fresh) turns out to already foreclose the cross-parameter
mixing hazard as a side effect, with nothing to add.

*But safety was the wrong question to stop at — profitability regressed real code.* The full
`challenges/` suite was re-run against a baseline built from unmodified `main` (`git stash` of the one
touched file, matching this document's own established practice of not needing a separate clone for a
pure source change). Every program's output was byte-identical (`cmp -s`, not eyeballed) in every
configuration tried below — this was never a correctness regression — but two of ten real benchmark
programs regressed in peak RSS, reproducibly, confirmed stable across repeated runs (not noise):
`challenges/1brc/brc.ash` at 10M rows, 6,970,236-6,971,004 KB baseline → 7,551,612-7,552,124 KB with
the veto removed (+~581 MB, +8.3%); at 100M rows, ~9,048,940 KB baseline → ~9,688,172 KB (+~639 MB).
`challenges/reverse-complement/reverse-complement.ash` (a 1,000,000-base FASTA input): 753,588 KB
baseline → 1,124,352-1,124,864 KB (+~370 MB, +49%). fannkuch-redux N=11 and binary-trees N=21 — the
two most historically sensitive sentinels — showed no change at all (256 KB constant and ~196,000 KB
respectively, matching baseline exactly), because neither program's own TCO loops ever have more than
one heap-typed parameter in the same frame, so the veto was never live for them either way; this is
exactly why the two regressions surfaced only in the two programs that happen to thread more than one
independently-classified heap accumulator through a single loop.

*Root cause, confirmed by IR-level classification dumps, not inferred.* Re-instrumenting (the same
temporary env-var-gated print, applied to both the veto-present and veto-removed builds via `git
stash` swaps for direct comparison) isolated the mechanism precisely: in `mergeEntries`, `entries`'s
`ConsumedListTailParams`-based eligibility and in `emit`, `buf`'s unconditional `Str` eligibility are
each ONLY reachable at all because their respective sibling (`acc`/`Trie`, `chars`/`List(Str)`)
independently fails every exemption — with the veto gone, both promotions fire correctly per
classifier A's own rules, but classifier A's rules answer "is this parameter's value, in isolation,
safely representable as RC" and never "is promoting it, in THIS frame, actually cheaper than leaving
it arena." Promoting a parameter to runtime-managed inside a frame that must ALREADY pay for a
per-iteration arena reset (because the sibling can never leave arena) adds a real, additional cost the
all-arena frame never paid: the promoted value must be defensively copied out of the about-to-be-reset
arena into RC space at every back edge (`EmitRuntimeManagedTcoDeepCopy`/`CopyOutList`/`CopyOutArena`,
the same machinery the original investigation found driving fannkuch's own residual leak before the
two "Phase 6 resolution" fixes closed it), regardless of whether the value was already fresh that
iteration. This cost scales with how large the promoted value grows and how many iterations the loop
runs — exactly `mergeEntries`' up-to-41,343-entry station table and `emit`'s per-character pass over
an entire FASTA sequence, and exactly why the two programs with more than one heap accumulator per
frame are the ones that caught it. This is a genuinely new axis this investigation surfaced that the
original design doc's own framing (§5 item 4, §6's veto discussion) did not anticipate: the CO-38/PR
#299 precedent the veto was reasoned about by analogy to is a SAFETY hazard (an arena parent's drop
silently failing to reach an RC child of the same nominal type); what actually regressed here is not a
safety failure at all (every output stayed byte-identical) — it is a PROFITABILITY failure, and the
veto's blunt "clear everything" turns out to double as an (apparently accidental) cost gate against it
that the four-classifier collapse, as originally scoped, has no replacement for.

*Two narrower carve-outs were tried and also did not produce a safe, valuable, landable slice.*
Rather than reverting immediately, two successively narrower attempts tried to keep SOME of the
veto's removal while avoiding the measured regressions:

1. Demote only the specific classification reason that caused `mergeEntries`' regression (a
   `List`-typed parameter eligible ONLY via the `ConsumedListTailParams` branch, never
   `FreshRebuiltListParams`/`AffineConsListParams`) back to arena whenever the frame still has a
   genuinely-failing sibling, while leaving every other eligibility reason (Str, BigInt, ADT, Tuple,
   Closure, and the other two List branches) fully unblocked. Implemented as a two-pass scheme:
   track which promoted slots were "cost-sensitive" (List-typed, consumed-tail-only) during the
   normal eligibility scan, then run the OLD veto's exact firing condition afterward but only strip
   the cost-sensitive slots back out, never the others, when it fires. This fixed `brc.ash` exactly
   (10M rows: 6,970,236 KB, byte-identical output, matching baseline to within measurement noise) —
   but `reverse-complement.ash` was untouched by this carve-out (still +~370 MB) because its own
   regressed parameter, `buf`, is a `Str`, not a `List` — proving the true cost-sensitive boundary is
   broader than "List consumed-tail promotions" specifically, and this narrower rule does not capture
   it.
2. Narrow further to preserve ONLY closure-typed (`TFun`) promotions specifically — the one
   eligibility reason the very first synthetic adversarial test above measured as genuinely free (no
   RSS difference, 30,976 KB vs 31,232 KB, across 2,000,000 iterations), on the theory that a fresh
   closure's own captured environment is typically small and bounded per iteration, unlike a growing
   string or a list of non-copy elements. Implemented by having `ClearRuntimeManagedTcoParams`
   preserve every slot already present in `tco.RuntimeManagedClosureParamSlots` instead of wiping it.
   A dedicated adversarial test was built to exercise exactly this shape (a closure accumulator
   rebuilt fresh at every tail self-call, alongside a self-recursive multi-constructor `Tree`
   accumulator of the same shape as `binary-trees`' own `Tree` — deliberately chosen so the failing
   sibling could never be confused with a scalar/arena-resettable type) and it FAILED even with the
   fix in place: direct instrumentation showed `RuntimeManagedClosureParamSlots` never contained the
   closure parameter at all, veto or no veto. The actual reason is a second, separate, pre-existing
   gap this investigation was not looking for: `LowerLambdaCoreRefreshRuntimeManagedTcoParams` (the
   pass that re-runs classification after the body is lowered, once parameter types have finally
   resolved from unconstrained type variables to concrete types) explicitly calls
   `LowerLambdaCoreIdentifyRuntimeManagedTcoParams(..., includeFreshClosures: false)` — deliberately
   excluding closures from re-classification at exactly the point their type would finally be concrete
   enough to match `TypeRef.TFun`. The FIRST (loop-entry) pass, where `includeFreshClosures` defaults
   to `true`, almost never sees a concrete `TFun` for an ordinary, unannotated closure parameter in the
   first place — confirmed directly: the same parameter's `parameterType` was printed as `TVar { Id =
   10 }` at loop-entry time and only resolved to a concrete `TFun { Arg = ...; Ret = ...}` by the
   second (closures-excluded) pass. Net effect: `RuntimeManagedClosureParamSlots` is essentially never
   populated for ordinary recursive closures regardless of the veto, so "preserve closures past the
   veto" has nothing left to preserve in realistic code — this carve-out is inert, not merely
   insufficient. This is a second, independent timing gap in the same family as classifier (D)'s
   already-documented one (`TrackRuntimeManagedTcoListPatternAliases` reading `RuntimeManagedParamSlots`
   before it is populated for the current body) but for the closure-eligibility check itself, discovered
   as a byproduct of this investigation rather than its target.

*All three attempts reverted in full.* Given neither the full removal (regresses two real programs)
nor either narrower carve-out (one is insufficient, the other inert) produced something both safe and
valuable, all three were reverted. The tree at the end of this investigation is byte-for-byte identical
to its start (verified: `git status`/`git diff` clean against the merged `main` tip this session began
from). No test files, instrumentation, or other artifacts from any of the three attempts survive.

*What is now confirmed, and what remains open.* Confirmed, with direct evidence rather than inference:
(a) the veto is live, real-world load-bearing code, not dead weight — it fires for the one nontrivial
program this repository ships as a benchmark, not merely for constructed examples; (b) per-parameter
mixing across unrelated types never produces a wrong value or a crash, at every scale tested (up to
2,000,000 iterations); (c) the specific "does mixing need group-level agreement" hazard this
migration's own established practice demands be checked directly (never assumed by analogy to the
ADT/List precedents, which each had a different, non-obvious answer) does NOT apply to TCO parameters
— the existing per-slot candidacy aggregation already forecloses a same-type cross-parameter swap
producing a mismatched representation, with nothing to add; (d) removing the veto is nonetheless not
safe to ship, because it is not a pure safety question — it trades the original mixing-safety concern
for a previously-unrecognized profitability one (a per-iteration defensive-copy cost the arena-only
frame never pays), and this cost was measured, not merely hypothesized, on two real challenge programs;
(e) the closure-eligibility path (`RuntimeManagedClosureParamSlots`) is latent, near-dead code for
ordinary unannotated recursive closures due to the `includeFreshClosures: false` exclusion described
above, independent of anything to do with the veto. What remains open and unstarted: the four-classifier
structural collapse and veto removal, exactly as titled at the top of this section and the original
Phase 6 section above. A future attempt should not re-run the same three shapes tried here without
first building the piece none of them had: a real signal for "would promoting this specific parameter,
in this specific loop, actually cost less than leaving it arena" — plausibly sourced from the promoted
value's own growth behavior across iterations (bounded vs. unbounded, the same distinction
`AffineStrParams`/`FreshRebuiltListParams` already draw for other purposes) or from proving a
promotion requires zero defensive copying because the value's own construction already lands in RC
form with no arena intermediate to copy out of — and should re-run this session's exact three
measurements (the Str/closure synthetic case, `challenges/1brc/brc.ash` at 10M and 100M rows, and
`challenges/reverse-complement/reverse-complement.ash`) as its own regression gate before considering
any variant of veto removal landable. The classifier (D) timing gap documented in the original Phase 6
section (`TrackRuntimeManagedTcoListPatternAliases` reading `RuntimeManagedParamSlots` before it is
populated for the current body) remains exactly as characterized there; this investigation did not
revisit it, beyond discovering its sibling gap in the closure-eligibility path described above.

**Phase 6, promotion-cost signal — a per-parameter profitability query built, not yet wired to any
decision.** The third attempt above ended by naming exactly what was missing before any further veto-
removal attempt: "a real signal for 'would promoting this specific parameter, in this specific loop,
actually cost less than leaving it arena.'" A follow-up session built that signal
(`Lowering.TcoPromotionCostSignal.cs`, queried via `Lowering.GetTcoPromotionProfitability(functionName)`)
as standalone, read-only analysis infrastructure — it changes no compiled output today; nothing in the
compiler consults it yet.

*Re-tracing the third attempt's own mechanism, from the actual emitted instructions rather than its
prose summary.* Before designing anything, the exact defensive-copy call sites the third attempt named
(`EmitRuntimeManagedTcoDeepCopy`/`CopyOutList`/`CopyOutArena`, reached through
`TcoBackEdgeNormalizeRuntimeManagedArg`) were re-read directly, and a temporary, env-var-gated
instrumentation hook (bypassing both veto call sites and tracing `EmitTcoBackEdgeArenaBlock`'s own
three-way dispatch, since removed) was built against a from-scratch synthetic repro shaped exactly
like `mergeEntries`/`Trie` (a `List((Str, Int))` consumed one cons cell at a time, alongside a
self-recursive two-constructor tree rebuilt fresh every iteration by a helper call). This confirmed the
third attempt's account of *where* the veto fires and *that* per-parameter mixing is safe, but found
one thing the third attempt's own prose did not state precisely: for this exact shape, tracing which of
`EmitTcoBackEdgeArenaBlock`'s three back-edge strategies actually fires shows the frame reaches neither
`TcoBackEdgeTryEmitRuntimeManagedReset`'s cheap per-parameter retain path NOR the "two-pass copy-out"
fallback (`GetTcoCopyOutKind`-driven) — it falls all the way through to `EmitTcoBackEdgeArenaBlock`'s
`!allCopyable` branch ("complex heap types — no arena reset") — and, critically, this happens
*identically* whether or not the consumed-tail list parameter is promoted to runtime-managed, because
both dispatch points key off `GetTcoCopyOutKind`/`CanArenaReset`, which are pure type-shape queries with
no dependence on which representation a value has been assigned. `TcoBackEdgeArgCopyOutKind` already
downgrades a consumed (non-freshly-rebuilt) list's `DeepAdt` kind to `None` for exactly the reason its
own in-code comment gives ("1brc's merge phase regressed ~400x" — a different, earlier incident than
this one), and that downgrade alone is enough to disqualify the whole frame's `TcoBackEdgeAllArgsCopyable`
check regardless of the tree sibling. So the specific per-iteration `EmitRuntimeManagedTcoDeepCopy` call
site the third attempt's diagnosis leads with is not, for this shape, actually the site where the extra
cost is added — the frame's back-edge reset strategy is provably unchanged by the promotion. The
additional cost the third attempt measured on the real binary must instead come from a *different*
consumer of `RuntimeManagedParamSlots` membership than the back-edge reset dispatch itself — most
plausibly the one-time loop-entry conversion of the value flowing in from the enclosing (non-tail) call,
and/or the per-node runtime-managed bookkeeping (header, `RcDup`/`RcDrop`) a cons cell now permanently
carries once promoted, that a plain arena cell never pays regardless of how the back edge resets. This
was not chased to a byte-level confirmation within this session's time (it would need its own
instrumentation of the loop-entry normalization path and the match/pattern-destructure `RcDup`/`RcDrop`
emission, not just the back-edge dispatch already traced) — it is reported as a correction to the third
attempt's account, not a refutation of its bottom-line finding (the two regressions are real,
reproducible, and cost-shaped, not safety-shaped): the *specific instruction sequence* the third
attempt's diagnosis names is confirmed real and exists, but is not the one that actually fires for the
`ConsumedListTailParams` shape specifically; a different, not-yet-instrumented consumer of the same
promotion decision is.

*The signal's design.* Rather than trying to price the not-yet-fully-traced downstream consumer
directly, the signal reuses a structural observation confirmed by the same tracing: in both of the
third attempt's real regressions (`mergeEntries`/`Trie` in `1brc`, and `emit`/`chars` in
`reverse-complement`), the promoted parameter's own classifier-A eligibility reason is one that is only
affordable *when the loop can actually reclaim between iterations* — a list walked one cons cell at a
time and never rebuilt fresh (`ConsumedListTailParams`, without also being `FreshRebuiltListParams`/
`AffineConsListParams`), or a string grown by repeated affine append (`AffineStrParams`) — and in both
cases a sibling parameter in the same frame (a self-recursive multi-constructor `Trie`, and a list that
is re-passed *unchanged* at one of its own two tail-call sites, which disqualifies it from
`ConsumedListTailParams` entirely per `CollectConsumedListTailParams`'s "every self-call site must agree
which variable is the tracked tail" rule) permanently fails every arena-reset and RC-eligibility
exemption the compiler already has (`CanArenaReset`, `IsResourceHandleType`,
`TcoContext.LoopInvariantParams`, classifier A's own per-parameter test). The signal
(`Lowering.RecordTcoPromotionProfitability`, computed once resolved parameter types are available,
alongside `LowerLambdaCoreRefreshRuntimeManagedTcoParams`'s own re-classification pass) asks, per
classifier-A-eligible candidate parameter: does any *other* parameter in the frame permanently block
every exemption (heap-typed, not a resource handle, not loop-invariant, not itself independently
RC-eligible) — with one deliberate exception: a closure (`TFun`) sibling never counts as blocking even
when it fails classifier A on its own, because a non-escaping closure is stack-allocated (a third,
zero-reset-cost representation, not arena-or-RC at all), so it cannot force the frame into the
"no reclaim at all" fallback the way a heap ADT/List sibling does — and *does* the candidate's own
eligibility reason belong to the "only affordable with reclaim" bucket. Both true → `NotProfitable`;
otherwise → `Profitable`. This factors classifier A's own per-parameter condition
(`Lowering.IsIndependentlyRcEligibleTcoParam`, extracted from
`LowerLambdaCoreIdentifyRuntimeManagedTcoParams` as a pure refactor, no behavior change) into a shared
helper the signal calls a second time, per this document's own repeated lesson about two classifiers
silently diverging on the same question — the signal asks classifier A's exact question about a
candidate's siblings, not a re-derived approximation of it.

*Validation.* Four C# unit tests (`TcoPromotionCostSignalTests.cs`) query the signal directly against
hand-built `.ash` sources shaped like the four required cases, each confirmed against the expected
answer: a `mergeEntries`/`Trie`-shaped loop (`entries` — `NotProfitable`), an `emit`/`chars`-shaped loop
(`buf` — `NotProfitable`), the original Str-accumulator-plus-sometimes-fresh-closure adversarial case
(`str` — `Profitable`, confirming the closure-exception is load-bearing: without it this case would
wrongly read `NotProfitable`), and a single-heap-parameter loop matching fannkuch-redux's own
`loop`/`State` shape (asserted `Profitable` if classifier A finds it eligible at all — a vacuous-sibling
sanity check that the signal does not touch what it shouldn't). Building the second and third of these
directly reproduced, independently, the exact type-resolution-timing family of gap this document's own
"Phase 6, third attempt" section already found for the closure-eligibility path: a parameter's type
(the tuple field of a consumed list element in one case; a closure's argument/return type in another)
can still read as an unresolved `TVar` at the exact point classifier A's own resolved-type pass runs,
unless something *within that same function's own body* forces unification early (a strict, non-deferred
builtin call; the deferred `+`-operator resolution path, `ResolveDeferredAdds`, evidently runs later
than this point for a value whose only Str/Float evidence is an unannotated `+` chain). This is not a
defect in the new signal specifically — classifier A's own resolved-type pass is subject to the same
gap, for the same reason — but it is a real, reproducible constraint on this signal's precision worth
recording plainly: for a TCO parameter whose eligibility-defining type is established *only* through an
unannotated `+` chain with no other constraining use in the same function body, the signal (like
classifier A itself) can under-fire (fail to recognize eligibility at all, the safe direction — a real
promotion opportunity looks like "not eligible," never a wrong verdict on an eligible one) rather than
misclassify. No case in the four validated here needed reaching further than this to get a correct
answer once the test sources were adjusted to include an ordinary, in-body forcing use (matching what
the real `1brc`/`reverse-complement` programs already do via `Ashes.Byte.fromText`/`io.write`).

*Scope discipline: no stretch goal attempted.* This session did not wire the signal into any veto or
classifier decision — the task was scoped to the signal itself, and the four-classifier collapse plus
veto removal genuinely needed to reattempt this after a signal exists (as the third attempt's own
closing note anticipated) is left for a future session, now with this piece available. The signal
touches no existing lowering decision: `RecordTcoPromotionProfitability` only reads facts other passes
already compute (`TcoContext`'s existing candidate sets, `CanArenaReset`, `IsResourceHandleType`,
classifier A's own condition) and writes to a new, otherwise-unconsulted table
(`Lowering._tcoPromotionProfitability`). Full validation: C# suite 1712/1712 (+4 new), LSP suite 52/52,
e2e suite 544/0 (unchanged), `dotnet format --verify-no-changes` clean, and a spot check (fannkuch-redux
N=8, checksum 1616/`Pfannkuchen(8) = 22`, peak RSS 256 KB — matching the post-"Phase 6 resolution, part
2" constant floor exactly) confirming nothing about ordinary compilation changed. The full
`challenges/` suite was not re-run at production scale beyond this spot check, since nothing in this
session changes any emitted instruction — the four-classifier collapse this signal exists to support
still needs that full gate (per the milestone-completion discipline already established for this
project) once it actually wires a decision to something.

*What remains for whoever wires this up next.* The signal answers "is promoting this parameter, given
its own eligibility reason and its siblings' own exemption status, expected to be profitable" — it does
not yet attempt to price the specific downstream mechanism (loop-entry conversion cost, per-node
`RcDup`/`RcDrop` bookkeeping) precisely enough to rank *how much* cheaper or more expensive a promotion
is, only whether it crosses from "adds bookkeeping with nothing to show for it" to "reaches a
representation that was going to be at least as cheap as arena regardless." A minimal wiring
(gate `LowerLambdaCoreRejectPartialRuntimeManagedTcoFrame`'s per-parameter clear by this signal's
verdict instead of clearing the whole frame) is the natural next step, but per this document's own
established gate, it must re-run this session's and the third attempt's exact regression measurements
(the Str/closure synthetic case, `challenges/1brc/brc.ash` at 10M and 100M rows, and
`challenges/reverse-complement/reverse-complement.ash`) byte-identical and with no time/RSS regression
before being considered landable, and it must also close the two type-resolution-timing gaps this
session reconfirmed (the closure-eligibility one already on record, and the deferred-`+`-resolution one
newly recorded here) if a wired decision is going to depend on seeing a resolved type at the point it
runs — a missed promotion because a type read as unresolved is conservative and acceptable; a *wrong*
promotion decision made against a stale, still-unresolved type would not be.

**Classifier (D) timing gap — landed; closure-eligibility dead code — investigated, left unfixed.** A
follow-up session picked up the two smaller, independent items the "third attempt" section above left
open: closing the classifier (D) timing gap itself (distinct from the four-classifier collapse, which
remains unstarted), and deciding whether the closure-eligibility gap discovered as that session's
byproduct is safely fixable on its own.

*Classifier (D): the direct-binding exclusion, replaced with a positive escape check, landed.* The
mechanism was re-verified against current `main` first and found unchanged from both write-ups above:
`TrackRuntimeManagedTcoListPatternAliases`'s eager check still reads `_tcoCtx.RuntimeManagedParamSlots`
before the refresh pass has populated it for an unannotated accumulator, so it still never fires, and
`ResolvePendingNestedTcoPatternAliasSites` still blanket-excludes every "direct" binding (one pattern
level below a declared TCO parameter) on the assumption the eager pass already protected it. A repro
was built and confirmed via direct IR inspection, not assumed: a `List(Str)` TCO parameter whose head is
bound directly (`s :: rest`) and embedded in a returned constructor (`Some(Box(v = s))`, `Box` a
single-field record) — chosen so the element type is pinned to `Str` from within the recursive
function's own body, since without that the parameter's type stays an unresolved type variable through
both classification passes and the parameter never becomes runtime-managed at all, which would make the
whole question moot. With the parameter genuinely runtime-managed, the emitted IR embeds `s`'s raw
pointer into the arena-built `Box` with no dup, and the loop's own exit drop (reached on the very first
iteration whenever the target is found immediately) decrements the same string's refcount before the
value ever escapes to the caller's own arena-to-RC copy-out — a live use-after-free shape, not a
theoretical one.

The fix: a new structural, pre-lowering analysis (`CollectEscapingDirectPatternBindings` in
`Lowering.Reuse.cs`, alongside the other AST-level `Collect*ParamProperty` helpers `LowerLetRecursive
LambdaValue` already runs before the body is lowered) walks every match directly on a declared TCO
parameter and flags a pattern-bound name as escaping unless every one of its appearances in that arm is
either the scrutinee of a further nested match on the same name (whatever that deeper match itself
binds gets its own, separate accounting through the existing chain-walk) or the bare, unchanged argument
at that same parameter's own slot in a tail self-call (already installed by the ordinary per-parameter
back-edge argument machinery regardless of this table). The result populates a new
`TcoContext.EscapingDirectPatternBindings` set. `ResolvePendingNestedTcoPatternAliasSites`'s filter
changed from unconditionally excluding every direct binding to excluding one only when it is NOT in this
set — closing the real gap without reintroducing the failure mode the design doc's own prior attempt at
this exact fix hit (see "Fix attempt 2" above): a direct binding that merely threads back unchanged, or
is only ever re-matched, is still excluded, exactly as before.

Deliberately conservative in one specific way: the "safe" shapes counted are exactly those two, nothing
more. A direct binding compared for equality, or passed to any function other than the enclosing
recursion's own self-call, counts as escaping even though a bare inspection never actually retains a
reference — this was found empirically while building the adversarial counter-test (see below), not
assumed: distinguishing "inspected" from "retained" would need the same per-value liveness/provenance
signal §5 item 4 and the "third attempt" section's closing paragraph both call out as the real
prerequisite for the eventual four-classifier collapse, and this fix does not attempt to build that.
The chosen tradeoff — an occasional redundant-but-harmless protective dup (a dup immediately followed,
somewhere down the line, by a balancing drop) rather than risk a sharper rule that could under-protect —
matches this phase's own explicit instruction to prefer a safe miss over a risky fix.

Validated: a new adversarial test (`NestedTcoPatternAliasTests.Direct_list_element_escaping_into_
returned_construction_gets_a_protective_dup`) fails without the change (confirmed by reverting the
filter and re-running: no `rc_tco_nested_alias_duplicated` label anywhere in the function) and passes
with it. `Copy_typed_tuple_field_is_not_protected_as_runtime_managed` — the exact PR #298 regression
test the design doc's own prior attempt at this fix broke — keeps passing unmodified (still exactly one
protected alias, `lit`; `pair` and `rest` are not swept in, because their only appearances are precisely
the two safe shapes above). Full gate: C# suite 1709/1709 (1707 baseline + 2 new), LSP suite 52/52, e2e
suite 544/0 (44 skipped, matching baseline), `dotnet format --verify-no-changes` clean. The four
sentinel challenge programs (`fannkuch-redux`, `binary-trees`, `1brc/brc.ash`, `reverse-complement`)
were each compiled at `-O2` from a baseline built via `git stash` of the touched files (matching this
document's own established practice) and from the fixed tree, then compared with `objdump -d`: all four
disassemble byte-for-byte identical apart from the embedded filename in the output header — none of
them exercises the specific shape this fix changes, so the fix is a proven no-op for every benchmark
this repository ships, not merely an untested one. `1brc/brc.ash` at 10M rows was additionally run end
to end from both binaries and its stdout compared with `cmp`: byte-identical.

*Closure-eligibility dead code: confirmed exactly as characterized, a naive fix crashes the compiler,
left unfixed.* The "third attempt" section's byproduct finding was re-verified precisely:
`LowerLambdaCoreRefreshRuntimeManagedTcoParams` still calls `LowerLambdaCoreIdentifyRuntimeManagedTco
Params(..., includeFreshClosures: false)`, and the loop-entry pass (where the parameter defaults to
`true`) still does not see a concrete `TFun` for an ordinary unannotated closure parameter — confirmed
by the same synthetic Str-plus-closure-accumulator shape the "third attempt" session used. Rather than
stop at re-confirming the diagnosis, this session tried the obvious candidate fix directly: flipping
that one call site's argument to `true`. It compiles, but **crashes the compiler itself** on that exact
synthetic program: `error: The given key '1' was not present in the dictionary.` — a `KeyNotFoundException`
out of `TcoContext.RuntimeManagedClosureActiveSlots`, thrown while lowering. Root cause, read directly
from the surrounding code rather than inferred: the loop-entry prologue
(`LowerLambdaCoreEnterTcoLoop`) allocates a `RuntimeManagedClosureActiveSlots` local for every slot in
`RuntimeManagedClosureParamSlots` **at that point** — before the body, and therefore before the refresh
pass, ever runs — and several later call sites (`EmitRuntimeManagedTcoExitParamDrop`'s closure branch,
among others) index that dictionary directly rather than through a `TryGetValue`/`GetValueOrDefault`
fallback. A closure slot the refresh pass discovers only after the body is lowered was never in the
entry-time set, so it was never given an active-slot local, was never entry-normalized into runtime-
managed form by the separate entry-prologue splice either, and the first direct dictionary index against
it throws. This is not a narrow, local patch away: fixing it for real needs the entry-prologue's active-
slot allocation and its entry-normalization splice both retrofitted to handle a closure parameter
discovered only at refresh time, which is itself new, untested surface added to the same subsystem this
whole phase's safety notes single out as its most dangerous. Per this phase's own standing instruction —
a missed optimization is always an acceptable place to stop; a new crash or unsound state is never
acceptable regardless of how small the triggering change looks — the flag flip was reverted in full and
nothing was shipped for this half of the investigation. `RuntimeManagedClosureParamSlots` remains exactly
as dead for an ordinary unannotated recursive closure as the "third attempt" section found it; a future
attempt should design the entry-prologue retrofit first and treat "does flipping `includeFreshClosures`
at the refresh call site still crash the same synthetic program" as its own first regression check,
before trying to measure any profitability gain from the classifier actually firing.

**Phase 6, profitability-gated veto relaxation — landed.** A fifth follow-up session did exactly the
minimal wiring the promotion-cost signal session's own closing note called for: gate
`LowerLambdaCoreRejectPartialRuntimeManagedTcoFrame`'s clear (and its resolved-type counterpart inside
`LowerCallTcoPromoteResolvedRuntimeParams`) per parameter, by `GetTcoPromotionProfitability`'s verdict,
instead of clearing every promoted parameter in the frame the moment any one of them is found blocking.

*Design.* Both veto call sites keep their original firing condition byte-for-byte unchanged — the
question "does some parameter in this frame permanently block a per-iteration arena reset" is exactly
as conservative as it always was, so nothing about when the veto fires changed. What changed is the
consequence of firing: instead of `ClearRuntimeManagedTcoParams` wiping every slot in
`TcoContext.RuntimeManagedParamSlots`, a new `DemoteUnprofitableRuntimeManagedTcoParams` walks only the
currently-promoted slots and clears a slot only when `ClassifyTcoParamPromotion` — the exact function
the read-only signal itself calls, not a re-derived copy — returns `NotProfitable` for it. A parameter
the signal finds `Profitable` keeps its runtime-managed representation even though a sibling in the
same frame permanently blocks the frame's own reset. The two veto call sites needed separate treatment
(one walks `scope` bindings with possibly-unresolved types at loop-entry time, the other walks already-
resolved `argTypes` at each back-edge call site) but funnel into the one shared demotion helper.

*The four known cases, re-verified against the real programs, not just the unit tests.* Re-running the
existing `TcoPromotionCostSignalTests.cs` cases first confirmed all four still resolve to their
documented verdict with this wiring in place. But per this document's own repeated lesson that a
signal validated only against hand-built `.ash` sources "shaped like" a real program is not the same
claim as validating it against the real program, the full `challenges/` suite was run next — and this
is where a real, confirmed gap in the signal itself turned up, not a wiring bug:

`challenges/reverse-complement/reverse-complement.ash`'s real `emit chars col buf` reproduced the full
~370MB regression the third attempt measured for the fully-removed veto, even with the profitability
gate wired in. Direct inspection (temporary instrumentation printing each parameter's verdict, since
removed) found `buf` classified `Profitable` — wrong, since `chars` still permanently blocks the frame
exactly as before. The reason: `ClassifyTcoParamPromotion`'s `costSensitive` check treated `buf` as
"affordable regardless of reclaim" because it is not a member of `TcoContext.AffineStrParams` — but it
fails that strict syntactic test for a reason specific to the *real* program that the hand-built unit
test's simplified source did not reproduce: `emit`'s column-wrap branch is `let _ = io.write(buf +
"\n") in emit(chars)(0)("")`, an intervening *read* of `buf` before resetting it to `""`, and
`CollectAffineAccumulatorsWalkLet` correctly disqualifies any candidate mentioned in a let-bound value
preceding a loop-continuing body — precisely because that read is a non-tail-position use the
affine-append proof cannot see past. The synthetic unit test's `then` branch omits that read
(`emit(chars)(0)("")` directly, no preceding `io.write`), so it never triggered the same disqualification
and the signal's own validation missed the gap. Auditing which of the four validated cases actually
exercised the "sibling blocks, but this candidate's own reason is affordable regardless" branch found the
answer was *none of them*: the brc/reverse-complement-synthetic cases both hit the `costSensitive ==
true` path, and the Str/closure and single-heap-parameter cases both have `hasBlockingSibling == false`
(the closure sibling is explicitly excluded from "blocking" by `IsPermanentlyBlockingTcoSibling`'s own
`TFun` carve-out, so they never reach that branch either) — the one branch responsible for the real
regression had zero positive evidence behind it and one concrete counterexample once checked against
real code.

*The fix: remove the exception rather than patch it further.* Given "a missed optimization is always
acceptable, a regression never is" and given the one real case exercising that branch was wrong, the
verdict was simplified to depend only on `hasBlockingSibling`: `NotProfitable` whenever a sibling
permanently blocks the frame, `Profitable` otherwise, full stop. This still passes every existing
`TcoPromotionCostSignalTests.cs` case (verified directly, not assumed) because the Str/closure case's
benefit never depended on the removed branch — it depended on `IsPermanentlyBlockingTcoSibling`'s own
`TFun` exclusion making `hasBlockingSibling` false in the first place, which is untouched. The remaining
net effect of this whole relaxation is therefore narrower than originally hoped: it helps exactly the
case that motivated wanting the veto gone (a closure that fails classifier A on its own no longer wipes
an unrelated, independently-eligible heap sibling's promotion) and nothing broader — every case where a
genuine (non-closure) heap sibling permanently blocks the frame now demotes every promoted parameter in
it, identically to the original all-or-nothing veto.

*A second, unrelated discovery made during this validation pass, reported not fixed here.* Re-measuring
fannkuch-redux N=11 as this document's own most-scrutinized sentinel found it was **not** constant at
256KB on the tree this session started from (`main` plus the classifier-D timing-gap fix immediately
above, `#317`) — it reproduced the exact N!-scaling leak shape "Phase 6 resolution, part 2" was supposed
to have closed, just roughly 43x larger per iteration (measured: N=8 ~54MB, N=9 ~554MB). Bisected
precisely across every commit between the last confirmed-256KB point and the tree this session started
from (rebuilding and re-measuring at each commit from a clean clone, not inferring): `73cf95f`
(match-scrutinee wrapper drop) — 256KB; `fa0a65b` (third attempt, reverted) — 256KB; `4bd0288` (`#316`,
promotion-cost signal) — 256KB; `4defd77` (`#317`, classifier D timing-gap fix, immediately above) —
regressed. `#317` itself is the cause, despite that PR's own validation claiming byte-identical
disassembly for fannkuch-redux specifically. Root cause, read directly from `#317`'s own diff: its new
`CollectEscapingDirectPatternBindings` (`Lowering.Reuse.cs`) flags a pattern binding extracted directly
off a declared TCO parameter as "escaping" — and therefore now eligible for a retroactively-spliced
protective `RcDup` that was previously always skipped for direct bindings — the moment it is used
anywhere beyond the two shapes that analysis treats as already safe (a further nested match on the same
name, or the bare unchanged argument at its own parameter's slot in a self-call). fannkuch-redux's own
`nextPerm` matches `st` (a TCO parameter) as `S(perm, count)` and passes `perm`/`count` into helper calls
(`rotateFirst`, `getAt`, `setAt`) — neither of the two safe shapes — so both now qualify for the new
retroactive dup. `#317`'s own writeup calls this tradeoff "a redundant-but-harmless protective dup (a
dup immediately followed, somewhere down the line, by a balancing drop)," but the measured behavior on
the real program contradicts that: the growth is unbounded and scales with the loop's own iteration
count exactly like a genuine leak, not a bounded one-time waste.

Because this regression lives entirely inside classifier D's own machinery — the single most
historically dangerous subsystem this document tracks (CO-8, CO-9, CO-28/29/32/34/35, `#298`, and now
this) — and is completely independent of the profitability-gated veto relaxation this session was
scoped to deliver (verified: the two changesets touch disjoint functions in `Lowering.cs`, confirmed by
temporarily reverting just the `#317` merge and re-validating this session's own commits cleanly on top
of the unaffected `#316` tip), no attempt was made to patch classifier D's own mechanism in this session.
`#317` remains merged on `main` with this regression live; whoever picks this up next should treat this
account, and the bisection commits above, as a ready-made starting point rather than re-deriving the
diagnosis from scratch — likely starting from `CollectEscapingDirectPatternBindings`'s own "inspected vs
retained" limitation that PR's own writeup already flagged as a known imprecision, now confirmed to be
more than just imprecision for this shape. This is reported as a discovery made *during* this session's
own required validation pass, not something this session set out to fix.

*Validation.* This session's own two commits were first validated in isolation against a corrected
(`#316`-only) base — built via a clean clone of `main` at the `#316` tip, confirming this session's
changes alone introduce no regression anywhere before layering `#317`'s independent, pre-existing
regression back on top for the final combined state. C# suite 1712/1712 against the `#316`-only base
(unchanged from that baseline — this relaxation added no new C# tests of its own, relying on the
existing `TcoPromotionCostSignalTests.cs` plus the challenges suite below), LSP suite 52/52, e2e suite
544/0, `dotnet format --verify-no-changes` clean. Full `challenges/` suite, every program run to
completion and compared byte-for-byte (`cmp -s`) against a baseline binary built from the unmodified
`#316`-merge tip, peak RSS via `/usr/bin/time -v`, not spot-checked: fannkuch-redux N=11 — 256KB constant
in both baseline and this relaxation (checksum `556355`, `Pfannkuchen(11) = 51`, unchanged — this
measurement predates layering `#317` back in, since `#317`'s own regression is independent of anything
tested here); binary-trees N=21 — 196,096 vs 196,352 KB (noise-level), byte-identical output; 1BRC 10M
rows — 6,971,004 vs 6,969,468 KB (noise-level); 1BRC 100M rows — 9,048,684 vs 9,049,196 KB (noise-level);
reverse-complement (1,000,000-base FASTA input) — 753,568 vs 752,992 KB (noise-level, and this is the
exact case that regressed before the `costSensitive`-branch removal above); mandelbrot N=4000, n-body
5,000,000 steps, spectral-norm N=5,500, pidigits N=10,000, k-nucleotide and regex-redux
(5,000,000-base FASTA input), and fasta 5,000,000 — all byte-identical output, all peak RSS within noise
of baseline. No program in the suite regressed from this session's own changes. Separately, compiling
fannkuch-redux with and without this session's changes on top of the `#317`-including tree produces a
byte-for-byte identical binary (confirmed by direct comparison, not just a size match) — fannkuch-redux's
own TCO frame has only one heap-typed parameter, so this relaxation's demotion logic has no sibling to
ever act on there, proving this session's changes are inert for that specific program regardless of
`#317`'s unrelated, pre-existing regression.

*What remains open.* The four-classifier structural collapse (A/B/C/D into one per-parameter decision)
remains fully unstarted — this session, like the promotion-cost-signal session before it, was scoped to
the veto side of Phase 6 only. The net value delivered is narrower than the original four-classifier
collapse would be: closure/heap-sibling mixing no longer trips the all-or-nothing veto, but a genuine
non-closure blocking sibling still demotes every promoted parameter in the frame, identically to before
— because no case has ever been found, on real code, where a promotion is profitable *despite* a
non-closure sibling permanently blocking the frame, and the one attempt to except such a case was wrong.
A future session attempting the full collapse should treat "does promoting parameter X pay for itself
given the ACTUAL downstream cost, not just whether some type-shape reason sounds affordable" as still
unanswered precisely — the signal identifies whether a sibling blocks the frame at all, not how to price
a promotion when there is no sibling to blame, which is the harder question the eventual per-parameter
decision (§5 item 4) still needs. The classifier-D regression discovered above is also unaddressed and,
given its severity and its location in the single most historically dangerous subsystem this document
tracks, should likely be the very next thing whoever owns `#317` looks at, ahead of any further Phase 6
collapse work.

**Classifier (D) escaping-call-argument gap — landed; corrects the prior session's own merge.** A
follow-up session found and fixed a live regression the "Classifier (D) timing gap — landed" session
above had itself introduced into `fannkuch-redux` — the one benchmark this whole document exists to
keep at a constant floor.

*Confirming the regression, independently, before trusting the report that named it.* A direct
build-and-run (`-O2`, the same binary+workload the rest of this document uses) reproduced it exactly
as described: N=8 peak RSS 54,272 KB, N=9 554,240 KB — both far above the 256 KB floor "Phase 6
resolution, part 2" established and confirmed constant through N=11. The growth ratio between N=8 and
N=9 (~10.2x) matches `N!/(N-1)! = N` for N=9, the same factorial-scaling signature this document's own
"Phase 6" residual-leak investigation already used to diagnose an unbounded per-outer-iteration leak,
not a fixed-size regression.

*Root cause.* `CollectEscapingDirectPatternBindings`'s two recognized-safe shapes (a further nested
match on the same name; the bare, unchanged argument at the same parameter's own slot in a tail
self-call) do not cover `nextPerm`'s and `loop`'s own shape: `perm`/`count`, extracted directly from
the `S(perm, count)` match on the `st` TCO parameter, are passed as arguments to plain helper calls
(`rotateFirst`, `getAt`, `setAt`) whose *results* (not `perm`/`count` themselves) are what get threaded
into the next iteration. Neither existing safe shape matches this, so the fix classified both bindings
as escaping and spliced a protective `RcDup` right after each pattern extraction, every outer-loop
iteration. Reading the resolved IR side by side with the actual runtime drop mechanism found why that
dup is not merely wasted, unlike the "occasional redundant-but-harmless dup" tradeoff the fix's own
writeup accepted: the parameter's real structural release (`EmitKnownConstructorRuntimeManagedAdtDrop`'s
per-field walk, reached either at the TCO back-edge or the function's exit) decides whether to
recursively free a list's cons cells or stop after a single decrement by checking `RcIsUnique` on the
list's own head cell. The spurious dup permanently pins that head cell's reference count at 2 instead
of 1, so the uniqueness check fails, the walk takes its conservative "shared, decrement once, do not
recurse" branch, and the entire remainder of that iteration's list is abandoned — every outer-loop
iteration, without bound. This is a genuine unbounded leak, not a wasted-but-balanced dup: nothing in
this shape ever drops the local pattern-binding's own slot independently (it is aliased to the TCO
parameter's own `OwnershipInfo`, not independently tracked), so the extra reference the dup adds is
never released by anything.

*The fix.* A third safe shape was added, checked as broadly and as safely as the existing two: a direct
binding whose every occurrence is a bare argument, at any position, to a call whose callee is not a
known constructor. This is sound independent of self-recursion — every call site's own
argument-passing convention already borrows the argument for the callee and only dups it if the
callee's own return value is found to retain it (confirmed directly in the resolved IR: a `Borrow`
followed by a conditionally-emitted `RcDup`), so the caller's own reference is left completely
unchanged by passing it into an ordinary call, regardless of what the callee does internally. A
constructor application is excluded because it is not a call in this sense: `Ctor(bindingName)` embeds
the binding's own reference, unchanged, directly into a new cell's field — exactly the shape that does
need the extra protective dup, since the field's original owner (the TCO parameter) and the new cell
are now two independent owners of what was one reference. The check is a purely syntactic comparison
against the real constructor-symbol table (not a naming-convention heuristic), matching the existing
self-call check's own syntactic rigor. `CollectEscapingDirectPatternBindings` and its helpers were
converted from static to instance methods to reach that table.

*Validated by hand against both existing regression tests before touching anything.* The new rule can
only ever move a binding from escaping to safe, never the reverse, and it excludes constructor
applications outright, so tracing it against `Copy_typed_tuple_field_is_not_protected_as_runtime_managed`
(the PR #298 regression the original broader attempt at this fix once broke) and
`Direct_list_element_escaping_into_returned_construction_gets_a_protective_dup` (the real escape this
fix's own predecessor closed) confirmed neither could regress: both tests' bindings were already
recognized safe (or, for the escaping one, still route through a constructor and stay excluded). Both
tests were then run and confirmed passing unmodified. A new adversarial test
(`Direct_binding_passed_only_to_a_plain_helper_call_is_not_protected_as_escaping`) pins the exact
`nextPerm`/`rotateFirst` shape — a direct list-cons binding fed only to a plain helper call, embedded
nowhere — and was confirmed to fail without the fix (the protective-dup label appears) before
confirming it passes with it.

*Measured result.* `fannkuch-redux`, before -> after this fix (same binary+workload as the rest of
this document, `-O2`):

| N | RSS before | RSS after | Checksum / Pfannkuchen |
|---|---|---|---|
| 8 | 54,272 KB | 256 KB | 1616 / 22 (unchanged) |
| 9 | 554,240 KB | 256 KB | 8629 / 30 (unchanged) |
| 10 | (factorial-scaling, untested) | 256 KB | 73196 / 38 |
| 11 | (factorial-scaling, untested) | 256 KB | 556355 / 51 (matches the historical floor exactly) |

RSS at every N is now 256 KB — the same constant floor "Phase 6 resolution, part 2" established,
confirmed unregressed rather than merely smaller.

Full validation: C# suite 1714/1714 (+1 new), LSP suite 52/52, e2e suite 544/0 (unchanged),
`dotnet format --verify-no-changes` clean. The full `challenges/` suite was re-run against a baseline
built from `origin/main` immediately before this fix (`4defd77`, i.e. with the classifier (D) timing
gap fix but not this correction): `binary-trees` N=21 (196,352 KB vs 196,096 KB baseline, noise-level,
byte-identical output), `1brc` (`m10000000.txt`/`m100000000.txt`, 10M and 100M rows — 6,971,004 KB /
9,048,684 KB baseline vs 6,971,004 KB / 9,048,172 KB fixed, noise-level, byte-identical output),
`reverse-complement` (fasta N=1,000,000 input — 753,760 KB baseline vs 753,636 KB fixed, noise-level,
byte-identical output), `n-body`, `spectral-norm`, `mandelbrot`, `pidigits`, and `fasta` all
byte-identical output with no RSS regression beyond the same noise band. `k-nucleotide` and
`regex-redux` (both fasta N=1,000,000 input) showed a real ~10% RSS difference against a baseline built
from the older pre-PR-#317 commit (`4bd0288`) the task's own instructions named as the comparison
point, but a direct rebuild from `4defd77` (immediately before this fix, i.e. after PR #317) reproduced
the same ~10% figure byte-for-byte with no dependency on this fix at all — confirmed by inspecting the
resolved IR directly for both binaries' `HashMap`-backed loops, which is byte-identical before and
after this fix. That difference predates this fix, is unrelated to it, and is out of scope for it;
reported here rather than silently absorbed into this fix's own numbers.

*A verification-methodology discrepancy, resolved rather than left open.* The "Classifier (D) timing
gap" session's own validation checked `fannkuch-redux` among its four disassembly-compared sentinels
and reported all four "byte-for-byte identical," which is why this regression was not caught at merge
time. Reproducing that exact check found the actual cause: these binaries are statically linked with no
ELF section headers at all, so `objdump -d` emits zero lines of disassembly for either input —
confirmed directly (`objdump -d <any binary produced by this compiler>` produces only the file-format
banner line, nothing else). A diff of that output is therefore trivially "identical" for any two
binaries this compiler produces, whether or not their actual machine code differs — the check that
session ran could never have detected a real difference, in `fannkuch-redux` or in any of its other
three sentinels, because the disassembler it relied on never disassembled anything. This is a real gap
in this project's own verification discipline, not specific to this one fix: any future session reusing
an `objdump -d` comparison against these binaries should confirm the tool actually produced
disassembly output before trusting a diff of it — a resolved-IR dump (as this fix's own validation
used) or a runtime measurement (RSS, checksum) are the load-bearing checks for this compiler's binaries,
not a disassembler that silently produces nothing.

**Phase 7 — Async/task-frame narrowing (Very large, most dangerous, do last).** Two sub-parts that
must both land together, not separately:
  (a) Replace `_usesAsync`/`_inCoroutineBody`'s whole-program scope with a per-value "does this
      value's live range cross an actual suspend point" signal, sourced from
      `StateMachineTransform`'s existing per-suspend-point liveness analysis.
  (b) **Before** (a) ships, fix `PerceusLifetimePlacement`'s CFG-blindness to `Suspend`/`Resume`
      boundaries (either by teaching its CFG builder about coroutine state transitions, or — the
      audit's preferred direction — by running the pass on the pre-`StateMachineTransform` linear
      instruction stream, before the coroutine split, so ordinary CFG reasoning naturally spans the
      await). **Landing (a) without (b) converts today's over-conservative leak-shaped failure mode
      into a use-after-free-shaped failure mode** — this is the single most load-bearing warning
      across all four audits and must be the release-gating check for this phase.
  (c) Design and land a **new** task-frame teardown hook (walk RC-owned payload slots and drop them)
      before any task-frame payload is allowed to become RC-owned — this mechanism does not exist in
      any form today (arena reclamation is bulk-region and genuinely doesn't need one), and its
      absence would otherwise leak every RC value captured by a cancelled/losing-race/never-completing
      task.
  New soak tests (heap value captured across an `await`, read on both sides; RC value captured by a
  task that never completes) must exist and pass *before* this phase is considered done — none exist
  today (confirmed by audit: every async e2e fixture only ever captures `Int` across an `await`).

## 7. Risks

**Two categories are genuinely NOT reducible to "ownership first, representation later" and need
explicit exceptions, not omissions:**

- **Bytes' borrowed mmap view.** A `Bytes` view over an `Ashes.IO.File.mmap`'d region is not
  separable into "ownership" and "representation" — the view's non-ownership *is* its
  representation (a raw pointer into someone else's mapping). This is correctly out of scope per the
  user's own framing (resources/foreign allocations are excluded), but the current code conflates
  "is this a view" with "is this RC-ineligible" via a producer-name exclusion list
  (`RuntimeManagedTcoListElementContainsBytes`) rather than a first-class tag on the value. The
  unification should give views a **structural, type-level tag** ("owned buffer" vs. "borrowed
  view") rather than continuing to detect them by producer-name walks — this is squarely in scope
  even though the view mechanism itself stays as-is.
- **Task-frame teardown on non-normal completion.** Discussed in Phase 7(c) above — this is net-new
  machinery, not a refactor of existing machinery, and is the single largest genuinely new piece of
  the whole plan.

**This is the highest-risk code in the compiler by the project's own repeated admission**
(`changelog.md:339-340`: "the reuse / lifetime analyses, where the CO-8/CO-9 use-after-frees lived").
Every phase above touches code with a specific, named historical incident:

| Phase | Historical precedent(s) | Failure mode |
|---|---|---|
| 3 (closures) | fannkuch-redux investigation (this session) | Silent leak from unrecognized-already-RC value — the borrow-path half of this phase closes the 47GB->27.4GB regression; the closure-forwarding half alone would not have (see §7) |
| 4 (ADTs/Tuples) | PR #299 / CO-38 | Silent leak from mixed arena/RC sibling representations |
| 5 (Lists) | CO-32's Phase A/B overlap ("readme_showcase priced 41.00 instead of 12.50") | Silent **wrong value**, not just leak — masked by optimized builds |
| 6 (TCO) | CO-8, CO-9, CO-28/29/32/34/35, PR #298 | Use-after-free / segfault / silent leak, six distinct historical incidents in this one subsystem |
| 7 (async) | (none yet — currently masked by over-conservatism, see §4) | Use-after-free if narrowed before the CFG-blindness fix lands |

**Discrepancy resolved (was open, now fully settled by Phase 3's actual measurement)**: PR #299's own
commit message attributed fannkuch-redux's regression to "a different, still-open bug in the RC
allocator's free-list reuse for list cons cells." A separate, earlier, never-landed experimental fix
(discarded diff) had instead pointed at two things together — the closure/function-result provenance
gap and a `LowerVar` `Borrow`-path condition — and measured a 47GB→27.4GB improvement from the
combination. Phase 3's formal, safety-reviewed re-implementation isolated the two: the
closure-forwarding mechanism alone gives only ~2%; the `Borrow`-path fix alone reproduces the full
47GB→27.4GB figure. **The free-list theory is now considered settled as incorrect** — the real
mechanism was the `Borrow`-path condition the whole time, confirmed via manual revert-and-rebuild
against the real benchmark. What remains unresolved is *only* the residual ~27.4GB — confirmed by
direct `FunctionResultProvenance`/`GetAdtField` inspection against the real fannkuch program to be
the match-extracted-field provenance gap (`GetAdtField` in `Lowering.Patterns.cs`) — a
`match`-destructuring question, distinct from both the closure-forwarding and `Borrow`-path
mechanisms already fixed. This remains unfixed. Whoever picks it up next should start from Phase 3's
PR discussion (direct `ASHES_EXPLAIN_OWNERSHIP=all` trace of
`nextPerm`/`rotateFirst`/`setAt`/`insertAt`/`loop` on the real
`challenges/fannkuch-redux/fannkuch-redux.ash`) rather than re-deriving the diagnosis from scratch.

**General mitigation for every phase**: this project's established discipline — "growing-key UAF
stress tests at every step," soak testing at 2K/10K/50K+ workload scales, verifying every new test
fails without its fix via `git stash` before trusting it, and the milestone gate already agreed for
the self-hosting effort (all `challenges/` benchmarks + full test suite must show no regression) —
should apply to every phase here, not just the self-hosting track.

## 8. Recommended implementation order

As enumerated in §6: **Phase 0 → 1 → 2 → 3 → 4 → 5 → 6 → 7**, with Phase 7 gated on its own two
sub-parts landing together (never (a) without (b)). Phases 1-3 are the highest-value, lowest-risk
starting point: mechanical, narrow blast radius, and each gives a real measured improvement while
proving the "unified ownership fact, consulted not re-derived" pattern — Phase 3 in particular
resolves the fannkuch-redux regression from ~47GB to ~27.4GB, Phase 6's interprocedural
call-result-freshness resolution (its own section above) takes it from ~27.4GB to ~1.56GB, and Phase
6's second resolution (the match-scrutinee wrapper-drop fix, its own section above) closes the
remaining ~1.56GB residual entirely — fannkuch-redux N=11 now matches the pre-regression ~3.8MB-class
historical baseline (measured 256KB, constant across N=8 through 11). A third Phase 6 session
attempted the four-classifier TCO collapse and veto removal themselves, confirmed removing the veto
is SAFE (never a wrong value or a crash, at every scale and in a directly-adversarial same-type-swap
test) but measurably regresses peak memory on two real `challenges/` programs (a previously
unrecognized profitability cost, not a safety one), and reverted all three attempts it tried in full
— see "Phase 6, third attempt" above. Two follow-up sessions then ran in parallel. One built exactly
the cost signal that finding called for ("Phase 6, promotion-cost signal" above) — validated against
both real regressions, the synthetic adversarial case, and a sanity check, but landed as a read-only
query only, not wired to the veto or classifier A. The other closed classifier (D)'s own timing gap (a
direct pattern binding off a TCO parameter that independently escapes now gets protected, via a new
structural escape check, without regressing the existing narrower guarantee) — see "Classifier (D)
timing gap — landed" above — and confirmed the closure-eligibility dead code found as the third
session's byproduct is not safely fixable without a larger, untested retrofit of the entry-prologue
machinery, so it remains exactly as dead as found. That timing-gap fix's own escape check was, however,
too narrow — it missed a direct binding fed only to a plain helper call, regressing `fannkuch-redux`
to unbounded per-iteration growth; a follow-up session closed that gap too (see "Classifier (D)
escaping-call-argument gap — landed" above), restoring the constant 256KB floor and, along the way,
finding that the timing-gap session's own four-sentinel disassembly check had been silently vacuous
(`objdump -d` produces no output at all for this compiler's binaries) rather than genuinely confirming
no change. A fifth session, running in parallel with the escaping-call-argument fix, wired the cost
signal into the frame veto ("Phase 6, profitability-gated veto relaxation" above): the veto's own
firing condition is untouched, but a parameter the signal finds `Profitable` now survives a sibling's
failure instead of being cleared along with it — but only after finding and correcting a real gap in
the signal itself (a cost-sensitivity exception with no real-program validation behind it, wrong on
reverse-complement's actual `buf`). The full structural collapse of classifiers A-D into one
per-parameter decision remains unstarted and is where the bulk of the remaining risk and effort live; a
future attempt now has the cost signal the third attempt found missing, but still needs to wire it in,
re-validate against the same regression gate, and close the type-resolution-timing gaps the signal's
own validation reconfirmed before landing any variant of veto removal. Phase 7 is deliberately last: it requires new test infrastructure that doesn't exist yet, touches the
one subsystem where the audits found a currently-*masked* bug (the CFG-blindness/whole-program-gate
interaction), and its failure mode if sequenced wrong is strictly worse (UAF) than the failure mode
it's fixing (leak).

This is multi-week/large-milestone-scale work in aggregate — not a sprint, and not a single PR. Per
the user's own instruction, do not force any phase past its audit's stated risk level if a
correctness issue is discovered mid-phase; stop and report, the same discipline already applied
throughout this audit.

## 9. Classifier collapse — decomposition plan

Six sessions have now worked on Phase 6 (TCO loop-carried values) without landing the collapse it was
titled for. Four of those sessions did real, validated, individually-useful work (interprocedural
call-result freshness, the match-scrutinee wrapper-drop fix, a promotion-cost signal, two rounds of
classifier-D timing-gap fixes) and two attempted the actual four-classifier collapse and were reverted
in full after real measurement. This section is a from-scratch decomposition of that remaining work,
produced by re-reading the current code (not the design doc's own paraphrase of it) call site by call
site, with every claim below traced to a specific file and line and spot-checked against the tree at
this session's start. It does not propose a clever shortcut — none was found — and says plainly, in
§9.6, exactly which parts of "collapse A/B/C/D into one per-parameter decision" remain genuinely large
even after this decomposition.

### 9.1 What each classifier actually is, re-verified against current code

| Classifier | Core defining site(s) | Approx. distinct read/write sites in `src/Ashes.Semantics` | The question it answers |
|---|---|---|---|
| A | `TcoContext.RuntimeManagedParamSlots` (`Lowering.Types.cs:41`); populated by `LowerLambdaCoreIdentifyRuntimeManagedTcoParams` (`Lowering.cs:6109-6152`) and its shared predicate `IsIndependentlyRcEligibleTcoParam` (`Lowering.cs:6163-6188`, whose own doc comment at `6153-6161` literally reads "Classifier A"); refreshed post-body-lowering by `LowerLambdaCoreRefreshRuntimeManagedTcoParams` (`Lowering.cs:5455-5481`) | ~25 | "Does this declared TCO parameter's own static type/usage shape license RC representation, considered alone?" |
| B | `GetTcoCopyOutKind` (`Lowering.Ownership.cs:3276-3322`) and the `TcoBackEdge*` family (`Lowering.cs:986-2164`), gated by `CanArenaReset` (`Lowering.Ownership.cs:1495-1499`) | ~30 functions, no mutable state of its own | "Given a value's TYPE alone, what copy-out/reset strategy (none, shallow memcpy, list walk, TCO-list-cell, recursive deep-copy) would be legal for it at a back edge?" — purely a representation query, never asked "is this value owned." |
| C | `_runtimeManagedResultTemps` (`Lowering.cs:550`, a per-lambda-body `HashSet<int>` of temp numbers), queried via `IsRuntimeManagedResultTemp` (`Lowering.cs:4725-4760`) and forward-propagated by `LowerLambdaCoreRefreshRuntimeManagedTcoResult` (`Lowering.cs:5508-5568`) | Several dozen, spread across nearly every lowering file (`Lowering.cs`, `Lowering.Ownership.cs`, `Lowering.Patterns.cs`, `Lowering.Symbols.cs`, `Lowering.Operators.cs`) | "Is this specific already-lowered IR temp's value, right now, runtime-managed?" — the compiler's single most-consulted ownership fact. |
| D | `_runtimeManagedTcoPatternAliases` (`Lowering.cs:189-190`) + `_activeRuntimeManagedTcoPatternAliases` (`Lowering.cs:191`, a genuinely distinct "currently holds an extra protective ref this iteration" flag set, not an alias for the dictionary) + `_pendingNestedTcoPatternAliasSites` (`Lowering.cs:219`) + `TcoContext.EscapingDirectPatternBindings` (`Lowering.Types.cs:86`, populated by `CollectEscapingDirectPatternBindings`, `Lowering.Reuse.cs:355-414`) | ~20, concentrated in `Lowering.Patterns.cs` (1103-1167) and `Lowering.cs` (5353-5453, 7468-7541) | "Does this value, extracted by pattern match from a runtime-managed TCO parameter, independently escape the current loop iteration and therefore need its own protective dup before the parameter's own drop can run?" |

One correction to the design doc's own catalogue, found while re-verifying: there is no `ClearRuntimeManagedTcoParams` (plural) function anywhere in the tree — only `ClearOneRuntimeManagedTcoParam` (`Lowering.cs:6257-6265`), called per-slot from `DemoteUnprofitableRuntimeManagedTcoParams` (`Lowering.cs:6239-6255`). Also, `TcoContext.EscapingDirectPatternBindings`'s own doc comment (`Lowering.Types.cs:73-86`) still describes only the two safe shapes from the "timing gap" fix — it was never updated when the "escaping-call-argument" fix added the third shape (`Lowering.Reuse.cs:526-533`); the code is correct, only that one comment is stale. Neither of these is a functional finding, but both matter for anyone reading the code cold before attempting the collapse.

### 9.2 Where two or more classifiers answer the same question, with evidence

This migration's own repeatedly-diagnosed disease ("two classifiers, same question, different code paths, can disagree") recurs at the TCO layer in (at least) five concrete places, each independently verified against current code:

1. **A and B both re-derive the same type-shape eligibility test, three times, independently.** `IsIndependentlyRcEligibleTcoParam` (`Lowering.cs:6163-6188`), `LowerCallTcoPromoteResolvedRuntimeParam`'s inline resolved-type re-check (`Lowering.cs:7574-7584`), and `TcoBackEdgeRuntimeManagedArgCanReset` (`Lowering.cs:1561-1582`) each independently test overlapping conditions — `CanCopyOutAdt`, `CanRuntimeManageTcoAdt`, `CanRuntimeManageOwnedTupleType`, `CanArenaReset`, and per-branch list-shape facts (`FreshRebuiltListParams`/`PassThrough`/`FreshListRebuild`/`ConsumedListTailParams`/`ConsumedListTail`/`SingleFreshCons`) — but as three separately-typed, separately-maintained predicate bodies rather than one shared function called three times. `IsIndependentlyRcEligibleTcoParam`'s own doc comment already names this exact hazard for a *different* pair (itself vs. the promotion-cost signal) and was fixed by extraction (`RecordTcoPromotionProfitability` calls the same function, `Lowering.TcoPromotionCostSignal.cs:119`) — the identical fix has not yet been applied to reconcile A against B.
2. **A and C are combined ad hoc, three times, at exactly the sites that decide whether a plain variable reference is "owned."** `LowerVar` (`Lowering.cs:2581-2600`) computes `runtimeManagedResult` as `_activeRuntimeManagedTcoPatternAliases.Contains(v.Name) [D] || ownerInfo is { RuntimeManaged: true } || _tcoCtx?.RuntimeManagedParamSlots.Contains(runtimeLocal.Slot) == true [A]`, then writes classifier C (`_runtimeManagedResultTemps.Add`, lines 2589/2599) — a three-classifier OR-then-write in 20 lines. The match-arm join (`MarkRuntimeManagedMatchResult`, `Lowering.Patterns.cs:71-83`, and its per-arm helper `MatchArmReturnsRuntimeManagedTcoParam`, `Lowering.Patterns.cs:85-96`) does the same OR between an inline classifier-C-like scan and a direct classifier-A read, for every arm, ANDed across arms. `LowerLambdaCoreEmitRuntimeManagedTcoExitDrops` (`Lowering.cs:6715-6768`) does the reverse combination at loop exit: reads C (`IsRuntimeManagedResultTemp(bodyTemp)`, line 6724) to decide the drop-vs-transfer strategy, then iterates A (`tco.RuntimeManagedParamSlots`, line 6732) to apply it per parameter.
3. **B's own `TcoBackEdgeArgCopyOutKind` downgrade exists specifically because A and B can disagree about what a value's construction history was.** The downgrade (`Lowering.cs:1852-1869`, comment quoted verbatim: "The whole-list DeepAdt clone is licensed per ARG, not per type... a threaded/consumed list... can share unbounded structure with the previous iteration — cloning it per back-edge multiplies the loop's cost by the list length (1brc's merge phase regressed ~400x)") is a hand-added correction bolted onto B precisely because B's pure type-shape answer and A's "was this rebuilt fresh this iteration" fact disagree for a consumed (not rebuilt) list, and nothing structurally prevents that disagreement from recurring for a shape this specific patch doesn't cover.
4. **D's own eligibility gate is entirely a read of A, at a point where A is provably not yet populated.** `TrackRuntimeManagedTcoListPatternAliases` (`Lowering.Patterns.cs:1103-1134`) gates on `_tcoCtx?.RuntimeManagedParamSlots.Contains(parent.Slot)` — but for an ordinary unannotated accumulator, `RuntimeManagedParamSlots` is only finalized by the refresh pass that runs *after* the whole body (including this exact match) has already been lowered. This is not merely "two classifiers asking the same question" but "one classifier asking another classifier a question that classifier cannot yet answer" — precisely the timing gap two of the six sessions already spent fixing (indirectly, via the separate `EscapingDirectPatternBindings` structural analysis) rather than fixing this gate itself.
5. **C's forward-taint pass and D's retroactive resolution are hand-sequenced to run in a specific order inside one function, because each is a precondition for the other's correctness, not because either was designed as a single pipeline.** `LowerLambdaCoreFinalizeTcoOwnership` (`Lowering.cs:5323-5337`) runs, in this exact order: A's refresh → D's `ResolvePendingNestedTcoPatternAliasSites` → a pending-flag resolution pass → A/B's entry-ownership splice → C's `LowerLambdaCoreRefreshRuntimeManagedTcoResult`. Reordering any two of these five calls would very plausibly reintroduce one of this subsystem's own historical bugs (CO-34(b)'s late-typed-accumulator leak and PR #298's use-after-free are both named in §2 of this document as consequences of exactly this "record now, resolve independently later" pattern) — the sequencing is load-bearing, undocumented as a dependency graph, and expressed only as call order in one function body.

### 9.3 Coupling graph

```mermaid
flowchart TB
    A["Classifier A\nRuntimeManagedParamSlots\n(static per-parameter shape)"]
    B["Classifier B\nGetTcoCopyOutKind / TcoBackEdge*\n(pure type-shape representation query,\nno mutable state)"]
    C["Classifier C\n_runtimeManagedResultTemps\n(per-temp reactive scan +\nforward-taint fixpoint)"]
    D["Classifier D\npattern-alias tables\n(escape analysis for\nmatch-extracted values)"]

    A -->|"IsIndependentlyRcEligibleTcoParam\nre-derives B's own shape facts\n(Lowering.cs:6163-6188 vs\nLowering.cs:1561-1582)"| B
    A -->|"TrackRuntimeManagedTcoListPatternAliases\ngates entirely on A\n(Patterns.cs:1108-1115),\ntimed BEFORE A is populated"| D
    A -->|"LowerVar / match-join OR classifier A\nwith C's write (Lowering.cs:2581-2600,\nPatterns.cs:71-96)"| C
    C -->|"PendingTcoReset seeds\nRuntimeManagedArgResults from C\n(Lowering.cs:7879), B's normalize\nwrites results back into C\n(Lowering.cs:1463/1484/1642/1700)"| B
    D -->|"LowerVar reads\n_activeRuntimeManagedTcoPatternAliases\nalongside A, feeds C's write\n(Lowering.cs:2582/2589)"| C
    B -->|"TcoBackEdgeArgCopyOutKind downgrade\nexists only because B disagrees\nwith A's fresh-vs-consumed fact\n(Lowering.cs:1852-1869)"| A

    VETO["LowerLambdaCoreRejectPartialRuntimeManagedTcoFrame\n+ DemoteUnprofitableRuntimeManagedTcoParams\n(reads A, consults promotion-cost signal,\nclears A's own membership)"]
    A --> VETO
    VETO --> A
```

### 9.4 Separability: which classifiers are closest, which is the odd one out

**B is the most separable, and arguably does not need to be "collapsed" at all.** Every predicate in
`GetTcoCopyOutKind`/`CanArenaReset`/the `TcoBackEdge*` dispatch chain was confirmed, by direct reading,
to depend only on `TypeRef`/`TypeSymbol` shape — never on `TcoContext`, `_runtimeManagedResultTemps`,
or any per-value ownership fact. `CanArenaReset` calls nothing else in this group; `GetTcoCopyOutKind`
calls only `CanArenaReset` and other pure shape predicates. This is, verbatim, the role §5 of this
document already assigns to "representation, chosen *after* ownership is decided": B already **is**
that downstream oracle, just not yet treated as one — today it's consulted through three
independently-phrased duplicate gates (§9.2 item 1) instead of one. The actionable finding is not "fold
B into the unified decision" but "stop re-deriving B's own answer by hand at A's and the back-edge's
own call sites, and call B directly instead."

**A and C are the closest pair, and the natural first real merge.** Both ultimately answer the same
question — "is this specific value, right now, runtime-managed" — just seeded from different starting
facts (A: static per-parameter classification; C: reactive instruction-shape scan + forward taint).
They are already directly OR'd together at the two sites in §9.2 item 2 with no abstraction boundary
between them; unifying those two sites into one shared "is this local/temp runtime-managed" query is a
change to *how the existing OR is expressed*, not a change to *what gets computed*, provided it's done
as a pure refactor first (mirroring the "safe reuse over a parallel rule" pattern already proven for a
different pair of classifiers during the Phase 6 interprocedural-freshness fix: `IsDirectRcConstruction`
was extended to fall back to `IsFreshRuntimeManageableAdtExpressionCore` rather than re-deriving a
parallel rule, specifically so a future change to one side "can never leave the interprocedural consumer
silently out of sync with it").

**D is the odd one out, and not just because it's the most-patched.** A, B, and C's three questions
("is this parameter's shape RC-eligible," "what copy-out strategy does this type support," "is this
temp currently runtime-managed") are all, at bottom, the *same kind* of question restated at different
granularities (parameter / type / temp) — which is exactly why unifying A+C is mostly a matter of
routing existing reads through one function. D's question — "does a value extracted by pattern match
from a runtime-managed parameter *independently escape* this iteration" — is a genuinely different kind
of question: a **liveness** question, not an **ownership-representation** question. Nothing in A, B, or
C, even fully unified, would answer it; today it is answered by a syntactic enumeration of "safe shapes"
(`Lowering.Reuse.cs:500-533`) that its own authors have twice found incomplete in opposite directions
(§9.5's account of the historical fixes) precisely because it is standing in for a liveness analysis
that does not exist yet. This is the same conclusion every one of the six Phase 6 sessions independently
reached from a different angle — the original investigation's closing note, the third attempt's closing
note, and the "timing gap" fix's own closing note all say, in different words, "this needs the
extended `FunctionOwnershipSummary`'s liveness answer, not another ad hoc condition." Nothing found in
this session's re-reading contradicts that; if anything, the concentration of coupling evidence in
§9.2 items 4 and 5 makes the case more precisely.

### 9.5 Why D must be last: the bidirectional-failure evidence, re-confirmed

Three fixes have now landed on classifier D's escape analysis in this document's own recent history,
and they are not all protecting the same invariant in the same direction:

- PR #298 (original) and the "timing gap" fix protect the **same** invariant — a pattern-extracted value
  must be protected from its parent TCO parameter's own drop before it escapes — with the timing-gap fix
  widening detection because the original mechanism structurally cannot fire for an ordinary unannotated
  accumulator (`Lowering.Patterns.cs:1108-1115`'s gate reads classifier A before A is populated for that
  body). Under-detection here is a **use-after-free**.
- The "escaping-call-argument" fix protects the **opposite** failure mode, introduced by the timing-gap
  fix's own conservative choice: over-classifying a binding as escaping installs a spurious `RcDup` that
  permanently pins a list head cell's refcount at 2, defeating `EmitKnownConstructorRuntimeManagedAdtDrop`'s
  `RcIsUnique` check and causing it to abandon (leak) the rest of the list every iteration — confirmed
  as the exact mechanism behind `fannkuch-redux`'s factorial-scaling regression between the two fixes.
  Over-detection here is an **unbounded leak**, via a code path (the drop walk's uniqueness check) that
  isn't part of classifier D at all.

Both directions have already shipped once, on `main`, inside a single-file change each time, and both
were caught only by real-scale RSS measurement — not by unit tests, not by disassembly comparison (the
same investigation that found the second regression also found the first regression's own validation
technique, `objdump -d` on this compiler's statically-linked binaries, silently produces zero
disassembly lines for any input, making its "byte-identical" claim vacuous by construction, not merely
insufficiently thorough). Any future change to D — including folding it into a unified per-value
decision — inherits this exact bidirectional fragility and this exact detection requirement: full
`challenges/` suite at production `N`, RSS numbers actually read from `/usr/bin/time -v` or equivalent,
not a disassembly diff and not a unit-test pass alone. This is why D is ordered last regardless of how
the rest of the sequence goes: it is the one classifier where "smaller, more careful" has already twice
failed to be automatically safe, and prerequisite infrastructure (a real liveness signal, per §9.4) does
not exist yet to make the next attempt structurally different from the last two.

### 9.6 Proposed sequence, honestly scoped

**Step 1 — Deduplicate the A/B type-shape predicate (mechanical, low risk).** Extract one shared
function covering the overlapping conditions currently duplicated across `IsIndependentlyRcEligibleTcoParam`
(`Lowering.cs:6163-6188`), `LowerCallTcoPromoteResolvedRuntimeParam`'s inline check (`Lowering.cs:7574-7584`),
and `TcoBackEdgeRuntimeManagedArgCanReset` (`Lowering.cs:1561-1582`); route all three through it. This is
the Phase-1-shaped move (mechanical, narrow blast radius, "consult a declared fact instead of
re-deriving from AST/type shape") applied to the one classifier pair (A/B) that is already
representation-only on one side.
- *What changes*: no behavior, if done correctly — this is a pure refactor. The risk is entirely in
  verifying "correctly": the three predicates are not textually identical today (they combine the same
  core shape facts with different additional conditions — A also requires membership in
  `FreshRebuiltListParams`/`AffineConsListParams`/`ConsumedListTailParams`; B's back-edge check tests
  `PassThrough`/`FreshListRebuild`/`ConsumedListTail`/`SingleFreshCons` against resolved facts instead of
  name-sets), so the shared function must be built by exhaustively enumerating every `TypeRef` shape
  category (`TStr`, `TBigInt`, every `TTuple`/`TNamedType` branch, every `TList` sub-branch, `TFun`) and
  confirming all three current call sites agree on each one *before* deleting any of the three original
  bodies — if they don't agree somewhere today, that disagreement is either a latent bug (fix it and
  treat the fix as its own, separately-validated change) or evidence the three questions aren't really
  the same question and shouldn't be merged after all.
- *Validation gate*: a new unit-test truth table (one assertion per `TypeRef` shape category, comparing
  old-inline vs. new-shared) confirmed to fail before the extraction and pass after, on top of the full
  `challenges/` suite (all ten programs, particularly `1brc` and `reverse-complement` — the two programs
  that already once caught a subtle A/B-adjacent profitability regression — at production scale, RSS via
  `/usr/bin/time -v`, byte-identical output via `cmp -s`) plus the full C#/LSP/e2e suites.
- *Scope*: genuinely single-session. It does not reduce classifier count (A and B remain two separate
  tables/mechanisms) — it removes one instance of duplicated derivation between them. Call this
  prerequisite cleanup, not "the collapse."

**Step 2 — Unify A and C at their two proven joint-decision sites (moderate risk, tight scope).**
Replace the ad hoc `D-then-A-then-write-C` combination in `LowerVar` (`Lowering.cs:2581-2600`) and the
`C-scan-then-A` combination in the match/if-join triad (`MarkRuntimeManagedMatchResult`/
`MatchArmReturnsRuntimeManagedTcoParam`, `Lowering.Patterns.cs:71-96`; `MarkUniformRuntimeManagedResult`,
`Lowering.cs:5226-5232`) with one shared "is this local/temp runtime-managed" query that both sites call,
instead of each re-deriving its own OR combination. Deliberately **do not** touch the other ~15
A-and-C-adjacent sites the audit found (TCO loop-entry splice, exit-drop pass, back-edge normalization,
etc.) in this step — those already route through A/C correctly via different, already-working
mechanisms, and widening this step's blast radius to "everywhere A and C are both mentioned" is exactly
the kind of scope creep this document's own history warns produces an unlandable single session.
- *What changes*: `LowerVar` and the match/if-join sites call one function instead of inlining the
  OR/AND logic; the underlying facts consulted (A's `RuntimeManagedParamSlots`, C's
  `_runtimeManagedResultTemps` via `IsRuntimeManagedResultTemp`) are unchanged.
- *Validation gate*: the existing `OwnershipTests`, `MatchScrutineeWrapperDropTests`, and
  `NestedTcoPatternAliasTests` suites (all three already exercise `LowerVar`/match-join paths directly)
  plus, at real scale, `fannkuch-redux` N=8 through N=11 (the sentinel most directly shaped by the two
  already-landed match-join/LowerVar-adjacent fixes — "Phase 6 resolution, part 2" fixed exactly a
  wrapper-drop bug in this neighborhood, making it the single most sensitive tripwire for a regression
  here) and `binary-trees` N=21 (the CO-38 sentinel, since match-arm joining is structurally close to
  binary-trees' own `Tree` recursion).
- *Scope*: plausibly single-session if — and only if — held strictly to these two sites. This is the
  same size of effort as the already-landed Phase 4/5 work (ADT/Tuple/List top-cell-freshness
  unification), which took a full session each and needed real re-verification passes; there is no
  reason to expect this pair to be easier, only that it is comparably sized, not smaller.

**Step 3 — Restructure the TCO orchestration to read one per-parameter table instead of five
hand-sequenced passes (large; NOT single-session; needs its own further split).** This is the actual
"four-classifier collapse and veto removal" Phase 6 was titled for, now scoped down to "A, B (via step
1), and C (via step 2) act as one input" while D is deliberately deferred to Step 4. Concretely:
`LowerLambdaCoreFinalizeTcoOwnership`'s five-call sequence (`Lowering.cs:5323-5337`, item 5 of §9.2) would
need to become a single pass producing one per-parameter verdict (RC / arena, with the promotion-cost
signal already built in `Lowering.TcoPromotionCostSignal.cs` folded in as the profitability half of that
verdict, replacing `DemoteUnprofitableRuntimeManagedTcoParams`'s current after-the-fact demotion), and
`LowerLambdaCoreRejectPartialRuntimeManagedTcoFrame`'s veto would need to be re-expressed as "no
per-parameter clearing at all, only the profitability-gated demotion already landed" — which the
document's own history shows was already attempted three times (full removal: safe but regresses `1brc`
and `reverse-complement`'s RSS; two narrower carve-outs: one insufficient, one inert) and once landed in
a genuinely narrower form (the profitability-gated relaxation). Given that history, this step should
itself split further, not be attempted whole:
- **3a (smaller, but still not proven trivial)**: re-verify whether the currently-landed
  profitability-gated relaxation can be widened now that steps 1-2 have removed the A/B and A/C
  duplicate-derivation risk that made the third attempt's own re-diagnosis (§"Phase 6, third attempt")
  hard to trust — re-run its exact three regression measurements (the Str/closure synthetic case, `1brc`
  at 10M/100M rows, `reverse-complement`) as the literal acceptance gate, since nothing about steps 1-2
  changes the actual profitability physics that caused those regressions.
- **3b (large, genuinely open)**: the actual orchestration rewrite (one unified per-parameter table
  replacing the five-call sequence). This needs a real design pass of its own — deciding what a
  "unified per-parameter TCO ownership record" looks like structurally, whether it lives on `TcoContext`
  or is computed by an extended `FunctionOwnershipSummary` per §5 item 4 — before any code is written,
  and should not be attempted in the same session as 3a.

**Step 4 — Fold classifier D into the unified per-value answer (highest risk; explicitly last; not
single-session; requires new prerequisite infrastructure that does not exist yet).** Per §9.4/§9.5, this
is not a refactor of duplicated logic (as steps 1-2 are) — it requires building a real liveness signal
("does this specific pattern-extracted binding's own live range end before the iteration completes,
independent of a fixed enumeration of syntactic shapes") that today only Steps 1-3's ownership
unification, extended with expression-level reach per §5 item 1, could plausibly provide. Concretely:
- **4a (shadow-compare only, no behavior change)**: mirroring Phase 0's own established practice
  ("compute it in parallel, assert-compare its answer against each existing classifier's answer in debug
  builds, and fix any disagreement found before anything depends on the new analysis"), build the
  liveness signal and run it, read-only, against every existing D-adjacent regression test
  (`NestedTcoPatternAliasTests`, the `fannkuch-redux`/`reverse-complement` sentinels) plus a purpose-built
  adversarial corpus enumerating every combination of the three current "safe shapes" and their
  negations, comparing its verdict against `CollectEscapingDirectPatternBindings`'s current verdict on
  each, before writing a single line that changes emitted IR.
- **4b (cutover, only after 4a soaks clean)**: replace the syntactic safe-shape enumeration with the
  liveness signal's verdict, re-running the full `challenges/` suite at production scale (RSS via
  `/usr/bin/time -v`, not disassembly) as the acceptance gate, given that the disassembly-based check
  used to validate one of D's own prior fixes was confirmed vacuous for this compiler's binaries.
Given that two of D's last two patches — each scoped, tested, and full-suite-validated at the time —
still shipped a regression caught only by a later session's own re-measurement, this step should be
budgeted as needing its own dedicated multi-session effort with a mandatory soak period between 4a and
4b, not estimated as a single sitting regardless of how well 1-3 go.

### 9.7 Honest summary

Of the four steps above, only **Step 1** is confidently single-session-sized as scoped. **Step 2** is
plausibly single-session but comparably sized to the already-landed Phase 4/5 work, which is not a small
bar. **Step 3** is explicitly not single-session and has already, across three independent sessions,
resisted being landed whole — this decomposition narrows it (3a vs. 3b) but does not make either half
small. **Step 4** is explicitly last, explicitly the highest risk, and explicitly requires prerequisite
analysis infrastructure that doesn't exist yet — estimating it as a single session would repeat the
exact mistake this document's own history warns against (an artificially small-sounding first step that
turns out to hide the real complexity). The honest bottom line: this decomposition makes the work
*sequenceable* and gives each step a concrete, evidence-based validation gate, but it does not make the
four-classifier collapse small. Roughly two-thirds of the risk and effort documented across all six
Phase 6 sessions to date lives in what this section calls Steps 3 and 4 — exactly the two steps this
section also declines to claim are single-session tasks.

## 10. Classifier collapse — Step 1 landed (A/B type-shape predicate dedup)

A parallel session produced a from-scratch decomposition of the remaining four-classifier-collapse
work (a "## 9. Classifier collapse — decomposition plan" section, its own PR not yet merged at the
time this session started, so its content was read directly from that PR's branch rather than from
`main`) and proposed a four-step sequence, Step 1 being: three call sites in `Lowering.cs` —
`IsIndependentlyRcEligibleTcoParam`, `LowerCallTcoPromoteResolvedRuntimeParam`'s inline resolved-type
check, and `TcoBackEdgeRuntimeManagedArgCanReset` — independently re-derive what should be one
type-shape question ("does this TCO parameter/argument's own type independently license
runtime-managed representation"), and should share one function. This section records that step
actually landing, plus a correction to how completely the three sites turned out to overlap.

**What was actually found, re-verified line by line against all three bodies.** The three predicates
are the same question, confirmed identical, for four of five type-shape categories:

- `TStr`/`TBigInt`: all three unconditionally `true`.
- `TTuple`: all three delegate to `CanRuntimeManageOwnedTupleType`.
- `TNamedType`: all three reduce to `CanCopyOutAdt(named, out _) || CanRuntimeManageTcoAdt(named)`.
  `IsIndependentlyRcEligibleTcoParam` carried a third, textually-distinct disjunct,
  `CanRuntimeManageAdt(ownedRecord)`, that looked like a real difference from the other two sites —
  it is not: `CanRuntimeManageTcoAdt` already calls `CanRuntimeManageAdt` as its own first disjunct
  (`Lowering.cs`, `CanRuntimeManageTcoAdt`'s one-line body), so `A || (A || B || C)` is just `A || B ||
  C` — the extra disjunct was dead weight, not a divergent rule, and removing it changes nothing.

For the remaining two categories, the three sites are genuinely **not** the same question — this
confirms, rather than corrects, the decomposition plan's own stated caution ("the three predicates are
not textually identical today... so the shared function must be built by exhaustively enumerating
every `TypeRef` shape category... if they don't agree somewhere today, that disagreement is either a
latent bug... or evidence the three questions aren't really the same question and shouldn't be merged
after all"):

- `TList`: `IsIndependentlyRcEligibleTcoParam` and `LowerCallTcoPromoteResolvedRuntimeParam` both
  consult the same three **whole-loop-body, static, per-parameter-name** classifications
  (`TcoContext.FreshRebuiltListParams`/`AffineConsListParams`/`ConsumedListTailParams`, looked up by
  the parameter's declared name, computed once before the loop's own body is lowered) — these two
  really were identical, just spelled with a name lookup in one and an index lookup in the other
  (`IsBorrowableInspectOnlyList` already has both overloads, one delegating to the other).
  `TcoBackEdgeRuntimeManagedArgCanReset`, however, consults four entirely different fields
  (`PendingTcoReset.PassThrough`/`FreshListRebuild`/`ConsumedListTail`/`SingleFreshCons`, each a
  **per-back-edge, per-argument** fact resolved fresh from the actual argument expression at that
  specific tail call — see `LowerCallTcoGatherResetFacts`). `ConsumedListTail` happens to be seeded
  from the same underlying name-set, but `PassThrough`/`FreshListRebuild`/`SingleFreshCons` have no
  analogue in the other two sites' name-sets at all; they answer "what did THIS call site's argument
  expression look like," not "what does the loop body's whole-program shape say about this parameter."
- `TFun` (closures): `IsIndependentlyRcEligibleTcoParam` gates on `TcoContext.FreshClosureParams`
  (again a static, whole-loop-body, per-parameter-name fact); `LowerCallTcoPromoteResolvedRuntimeParam`
  has no closure branch at all (closure promotion for a resolved argument type is handled by a
  different, already-existing code path around `LowerCallTcoEvalArg`'s own `freshClosure` check,
  `Lowering.cs:7731-7734`, which requests closure allocation directly rather than going through this
  predicate — folding a redundant closure branch into this site would be inert, not incorrect, but
  was left alone rather than added speculatively); `TcoBackEdgeRuntimeManagedArgCanReset` uses
  `PendingTcoReset.RuntimeManagedArgResults`, again a per-back-edge resolved fact, unrelated to
  `FreshClosureParams`.

**Consolidation landed.** Extracted `IsRcEligibleScalarTupleOrAdtType(TypeRef)` — exactly the four
identical categories above, with the redundant ADT disjunct dropped — and routed all three call sites
through it for those categories. The `TList`/`TFun` branches were deliberately left exactly as they
were at each of the three sites (still separately coded, still consulting their own genuinely different
signal), with the reason documented inline at the new shared function's own doc comment rather than
merged, per the decomposition plan's own explicit permission to report a real difference instead of
picking one arbitrarily. This is a smaller consolidation than "one function answers the whole
question" — it removes one confirmed instance of the duplicated-derivation disease for 4 of 5 shape
categories, and leaves classifier A and classifier B's own genuinely distinct list/closure facts
exactly as separate as they always were. Net change: roughly 30 lines added (one new shared method
plus its doc comment), roughly 25 lines removed (the three old inline copies of the identical portion),
across `src/Ashes.Semantics/Lowering.cs`.

**Validation.** A new reflection-based truth-table test
(`src/Ashes.Tests/TcoRcEligibilityPredicateTests.cs`) pins the shared predicate directly: plain scalars
false, `Str`/`BigInt` true, a copy-safe tuple true vs. one containing a function element false, a
single-constructor record of copy-safe fields true (via the shallow-copy-out path), a
single-constructor record with a `Str` field true (via the runtime-managed-dropper path, proving the
now-removed disjunct's own case is still covered by `CanRuntimeManageTcoAdt`), a multi-constructor ADT
whose fields are all independently copy-safe false (nothing to protect, so no promotion is licensed),
and a synthetic zero-constructor type false. Full gate, run from a tree with the LLVM native runtime
and the repo's own 1BRC 10M/100M-row fixtures already available locally (no re-download needed):

- `dotnet build Ashes.slnx`: clean.
- C# suite: 1722/1722 passed (0 failed, 0 skipped), including the new test file and every test named
  in this step's own safety brief (`NestedTcoPatternAliasTests`, `MatchScrutineeWrapperDropTests`,
  `OwnershipTests`, `OwnershipProvenanceTests`, `TcoMixedOwnershipTests`, `TcoPromotionCostSignalTests`).
- LSP suite: 52/52 passed.
- e2e suite (`dotnet run --project src/Ashes.Cli -- test tests`): 544 passed, 0 failed, 44 skipped
  (platform-gated tests, unrelated to this change).
- `dotnet format Ashes.slnx --verify-no-changes`: clean.
- Full `challenges/` suite, compared against a baseline built from `origin/main` at the commit this
  session started from (`1e68013`), same machine, same `-O2`, both compiled via a plain `git clone`
  of the working tree (not a second `git worktree`) checked out to that commit: **all eleven compiled
  binaries — `binary-trees`, `fannkuch-redux`, `fasta`, `k-nucleotide`, `mandelbrot`, `n-body`,
  `pidigits`, `regex-redux`, `reverse-complement`, `spectral-norm`, and the 1BRC `brc` binary — are
  byte-for-byte identical to their baseline counterparts (`cmp -s`), not merely output-identical.**
  This is a stronger check than the byte-identical-output/RSS-measurement bar this step's own brief
  asked for (and stronger than a resolved-IR dump, which was rejected as a check for a different
  reason earlier in this document — a full ELF `cmp` has no analogous blind spot). Runtime spot
  checks on top of the binary identity, all matching within normal single-run noise: `fannkuch-redux`
  N=8/9/10/11 all resident at the documented constant 256 KB floor (checksums 1616/8629/73196/556355,
  `Pfannkuchen` 22/30/38/51, matching the README exactly); `binary-trees` N=21 at 196,352 KB (~191.75
  MB, matching the README's ~191.8 MB) with byte-identical stdout against the baseline binary;
  1BRC at 10,000,000 rows (6,970,836 KB vs. baseline 6,970,176 KB, a ~0.01% run-to-run difference) and
  100,000,000 rows (9,048,772 KB vs. baseline 9,048,880 KB, ~0.001%), both with byte-identical output;
  `reverse-complement` on a 1,000,000-sequence FASTA input (753,588 KB vs. baseline 753,080 KB,
  ~0.07%) with byte-identical output. Every other challenge's stdout was also confirmed byte-identical
  against its baseline run (`mandelbrot`, `n-body`, `pidigits`, `k-nucleotide`, `spectral-norm`,
  `regex-redux`).

**Honest scope assessment.** The decomposition plan called Step 1 "confidently single-session-sized as
scoped," with the caveat that the risk was entirely in verifying the three predicates actually agreed
category-by-category before deleting anything. That is exactly how this session went: the code change
itself is small (one extracted method, three call sites routed through it), but a meaningful fraction
of the session's effort went into confirming, rather than assuming, that the ADT disjunct really was
redundant (it required tracing `CanRuntimeManageTcoAdt`'s own definition, not just reading the caller)
and that the list/closure branches were genuinely NOT the same question rather than a divergence to
paper over (it required tracing `PendingTcoReset`'s field-population site,
`LowerCallTcoGatherResetFacts`, back to confirm those facts really are per-back-edge and not just a
renamed copy of the static per-parameter-name sets). Step 1 turned out exactly as small as the
decomposition plan estimated, provided the verification work is treated as the actual deliverable and
the diff size is not — a two-line diff with an unverified "they're the same" assumption behind it
would have been the fast, wrong way to do this same step. Step 2 (unifying classifier A and C at the
`LowerVar`/match-join sites) remains the next candidate; Steps 3 and 4 are untouched by this session.

## 11. Classifier collapse — Step 2 landed (A/C consolidation at LowerVar and the match/if-join sites, smaller than proposed)

A session picked up Step 2 as proposed above: unify classifier A and C at the two sites where they are
combined ad hoc — `LowerVar` and the match/if-join triad. As with Step 1, the actual current code was
re-read line by line rather than trusting this document's own paraphrase, and the result is honestly
smaller than "one shared query, two call sites": the three named call sites turned out to be doing three
structurally different jobs, of which only one narrow fragment is genuinely identical text. That
fragment has been extracted and shared; the rest has been left exactly as it was, with the reasons
documented here rather than papered over.

**What was actually found, re-verified against the tree at this session's start (`dc3c9e9`, this
document's own §10 landing).**

- `LowerVar` (`Lowering.cs:2557-2602`, unchanged from this document's own citation) is not a site that
  *reads* an existing classifier-C fact and ORs it with classifier A — it is one of classifier C's own
  *population* sites. It computes a three-way OR — the pattern-alias set (the mechanism this document
  calls classifier D) `||` a per-binding ownership fact recorded when the binding was first tracked
  (`ownerInfo is { RuntimeManaged: true }`, populated far away at each binding's own `TrackOwnedValue`
  call from that binding's construction-expression shape — a genuinely separate fact from either A or C,
  not merely an alias for one of them) `||` classifier A's slot-membership test — and then writes that
  OR's result into `_runtimeManagedResultTemps`, i.e. classifier C's own table. So "unify A and C" does
  not describe what happens at this site at all: A is one of three ingredients being folded into a fresh
  C entry, not combined with a pre-existing one.
- The match-arm join (`MarkRuntimeManagedMatchResult`/`MatchArmReturnsRuntimeManagedTcoParam`,
  `Lowering.Patterns.cs:71-96`, also unchanged from this document's citation) is structurally the
  opposite: it reads an already-computed per-arm flag (populated earlier, in `LowerMatchArmsLinear`
  around `Lowering.Patterns.cs:623-632` — outside this step's two named line ranges) and ORs it with a
  direct classifier-A peek at the arm's own trailing AST node. The per-arm flag itself is a mix of a
  classifier-C read (`_runtimeManagedResultTemps.Contains`) and an inline instruction-shape scan.
- A genuine correction to this document's own characterization, in the same spirit as §10's correction
  about the redundant ADT disjunct: `MarkUniformRuntimeManagedResult` (the if-join site, `Lowering.cs:
  5224-5230`) does **not** perform an ad hoc "C-scan-then-A" combination at all. Reading it directly:
  it calls `IsRuntimeManagedResultTemp` — the one canonical, already-shared classifier-C query used
  throughout the rest of the file — on both branch temps and ANDs the two results. There is no separate
  classifier-A read anywhere in this function. This document's own §9.2/§9.6 text bundled it into "the
  match/if-join triad" performing the same ad hoc combination as the other two sites; that bundling does
  not hold up against the current code. Nothing needed consolidating here: this site already **is** the
  shared query the rest of this step is trying to establish elsewhere.
- A latent, pre-existing possible coverage gap was found, and deliberately **not** touched: the per-arm
  scan feeding `MarkRuntimeManagedMatchResult` (`Lowering.Patterns.cs:623-631`) recognizes only two
  instruction kinds (`AllocAdt`, `AllocReusing`) when checking whether an arm's own copy-out produced a
  fresh runtime-managed value, whereas the canonical `IsRuntimeManagedResultTemp` (`Lowering.cs:
  4723-4758`) recognizes roughly twenty-three. Routing that per-arm scan through the canonical query
  would very plausibly widen which match results get classified as uniformly runtime-managed — a real
  behavior change, not a refactor, and squarely the kind of change this step's brief said not to make.
  It is also outside both of this step's two named line ranges (it lives in `LowerMatchArmsLinear`, not
  in the join functions named by §9.6). Left exactly as it is; flagged here as a candidate for a future,
  separately-scoped and separately-validated investigation, not attempted in this session.

**Consolidation landed.** The only fragment confirmed genuinely identical at both of this step's named
sites is the classifier-A slot-membership test itself, applied to a `Binding.Local`:
`_tcoCtx?.RuntimeManagedParamSlots.Contains(slot) == true`. `LowerVar` had it inline as one arm of its
three-way OR; `MatchArmReturnsRuntimeManagedTcoParam` had the identical expression, reached via its own
`Lookup` instead of an already-resolved `Binding.Local`. Extracted `IsRuntimeManagedTcoParamSlot
(Binding.Local)` (`Lowering.cs:2557-2566`) and routed both through it — a literal, provably
behavior-identical extraction (same field, same containment call, same operand), not a widening of
either site's combination. `LowerVar`'s other two OR ingredients (the pattern-alias set and the
per-binding ownership fact) and the match-arm join's per-arm classifier-C-derived flag are untouched,
because they are not shared with anything at the other site — folding either site's extra facts into
the shared predicate would change what the *other* site computes, which is exactly the kind of scope
creep this step's brief warned against forcing.

**Validation.** Full gate, run from a tree with the LLVM native runtime and the repo's own 1BRC
10M/100M-row fixtures already available locally (copied from a sibling worktree's `runtimes/`
directory rather than re-downloaded):

- `dotnet build Ashes.slnx`: clean.
- C# suite: 1722/1722 passed (0 failed, 0 skipped), including every test named in this step's own
  safety brief that actually exists in the tree today: `NestedTcoPatternAliasTests` (4),
  `MatchScrutineeWrapperDropTests` (5), `OwnershipTests` (142), `OwnershipProvenanceTests` (12),
  `TcoPromotionCostSignalTests` (4), `TcoRcEligibilityPredicateTests` (8, this document's own §10
  addition). One name in the brief, `TcoMixedOwnershipTests`, does not correspond to any class in the
  current tree (`grep -rn TcoMixedOwnership src/` finds nothing) — noted here rather than silently
  ignored, per this document's own standard of reporting what was actually found rather than what was
  expected.
- LSP suite: 52/52 passed.
- e2e suite (`dotnet run --project src/Ashes.Cli -- test tests`): 544 passed, 0 failed, 44 skipped
  (platform-gated, unrelated to this change), including every `tco_*.ash` (15), `reuse_*.ash` (27),
  `ownership_*.ash` (1), and `runtime_rc_*.ash` (7) file re-run explicitly by name as an extra check on
  top of the full-suite run.
- `dotnet format Ashes.slnx --verify-no-changes`: clean.
- Full `challenges/` suite, compared against a baseline built from `origin/main` at the commit this
  session started from (`dc3c9e9`, this document's own §10 landing), same machine, same `-O2`, the
  baseline built from a plain `git clone` of the repo (not a second `git worktree`) checked out to that
  commit, per this document's own established practice: **all eleven compiled binaries —
  `binary-trees`, `fannkuch-redux`, `fasta`, `k-nucleotide`, `mandelbrot`, `n-body`, `pidigits`,
  `regex-redux`, `reverse-complement`, `spectral-norm`, and the 1BRC `brc` binary — are byte-for-byte
  identical to their baseline counterparts (`cmp -s`).** Runtime spot checks on top of the binary
  identity (necessarily identical to their own baseline runs, since the binaries themselves are
  identical, but re-measured directly rather than assumed): `fannkuch-redux` N=8/9/10/11 all resident at
  the documented constant 256 KB floor (checksums 1616/8629/73196/556355, `Pfannkuchen` 22/30/38/51,
  matching both the README and this document's own §10 measurement exactly); `binary-trees` N=21 at
  196,096 KB (~191.5 MB, matching §10's 196,352 KB within ordinary run-to-run noise) with byte-identical
  stdout against the baseline binary; 1BRC at 10,000,000 rows (6,969,920 KB vs. §10's 6,970,836 KB) and
  100,000,000 rows (9,048,624 KB vs. §10's 9,048,772 KB), both with byte-identical stdout;
  `reverse-complement` on a freshly generated 1,000,000-sequence FASTA input (753,984 KB vs. §10's
  753,588 KB) with byte-identical stdout. The remaining six challenges (`mandelbrot`, `n-body`,
  `pidigits`, `k-nucleotide`, `spectral-norm`, `regex-redux`) were not independently re-run at full
  scale: byte-for-byte identical compiled binaries cannot produce different output for the same input,
  so the `cmp -s` result already subsumes what a separate stdout comparison would show for them.

**Honest scope assessment.** This document's own §9.6 called Step 2 "plausibly single-session... there
is no reason to expect this pair to be easier, only that it is comparably sized" to the Phase 4/5 work.
That estimate assumed the two named sites really were both instances of "OR classifier A with classifier
C" — re-reading the current code directly shows that assumption was not quite right: `LowerVar` is a
classifier-C *producer* folding in three facts (one of which, the per-binding ownership flag, is not
literally classifier A or C at all), while the match-arm join is a classifier-C *consumer* combined with
an independent classifier-A fallback, and the if-join site turned out to need no work at all because it
was already routed through the canonical shared query. Only one thin, unambiguous fragment was common to
both of this step's two named sites, and it has been extracted and shared with zero behavioral change
(confirmed at the strongest available bar: byte-identical compiled output, not disassembly, not just
unit tests). This is a smaller, more honest result than "the two sites now share one combined query" —
matching this document's own §10 precedent of reporting a partial, correctly-scoped consolidation rather
than forcing a merge the underlying code does not actually support. The remaining, larger question this
step does not resolve — whether `LowerVar`'s three-way OR and the match-arm join's flag-plus-fallback
should ever be restructured into one true shared "is this value runtime-managed" query, as opposed to
sharing only their one common sub-fact — is left to Step 3, where §9.6 already anticipated the
orchestration-level rework would need its own design pass; nothing found in this session narrows that
remaining work, only clarifies precisely where today's boundary between classifiers A and C actually
sits at these two sites.

## 12. Classifier collapse — Step 3a: re-verifying the profitability-gated veto relaxation's scope (no widening landed)

§9.6 scoped Step 3a as: re-check whether the currently-landed profitability-gated relaxation ("Phase
6, profitability-gated veto relaxation" above) can be widened now that Steps 1-2 removed the A/B and
A/C duplicate-derivation risk that made the third attempt's own re-diagnosis hard to trust, re-running
its exact three regression measurements as the acceptance gate. A session picked this up starting from
the Step 2 tip (`0fc99cf`), which is also `origin/main`'s tip at the time this session started — the
relaxation from `#318` and both classifier-collapse steps are already merged; nothing about the tree
needed to change before the re-verification could begin.

**Tracing whether Steps 1-2 actually touch anything the veto/profitability signal depends on, rather
than assuming they do because both are "classifier work."** The veto's own firing condition
(`LowerLambdaCoreRejectPartialRuntimeManagedTcoFrame`/`LowerCallTcoRejectPartialResolvedRuntimeManagedTcoFrame`,
`Lowering.cs:6225-6257`/`7634-7666`) and the profitability signal's own two functions
(`IsPermanentlyBlockingTcoSibling`/`ClassifyTcoParamPromotion`, `Lowering.TcoPromotionCostSignal.cs:154-187`)
were re-read directly against the two extractions Steps 1-2 actually landed:

- Step 1's `IsRcEligibleScalarTupleOrAdtType` (`Lowering.cs:6212-6216`) **is** on the profitability
  signal's own call path — `IsPermanentlyBlockingTcoSibling` calls `IsIndependentlyRcEligibleTcoParam`
  (`Lowering.cs:6172-6194`), which calls the shared predicate for the `TStr`/`TBigInt`/`TTuple`/
  `TNamedType` branches. But §10's own landing already proved this extraction behavior-identical for
  exactly those four categories (confirmed there by exhaustive category-by-category comparison and, at
  the strongest available bar, eleven byte-for-byte identical compiled binaries against a same-commit
  baseline) — a proven no-op cannot have changed what the veto or the profitability signal compute,
  and re-confirming that proof wasn't necessary a second time; what needed checking was only whether
  it's genuinely a no-op on this exact call path too, which the category-by-category argument already
  covers since `IsIndependentlyRcEligibleTcoParam` is one of the three original call sites §10 verified.
  The `TList`/`TFun` branches — the ones §10 found were **not** actually the same question at the three
  original sites and therefore deliberately left unmerged — remain exactly as separate today as they
  were before Step 1, so nothing about the one classifier pairing (list/closure eligibility) most
  entangled with the profitability signal's own closure carve-out (`IsPermanentlyBlockingTcoSibling`'s
  `TFun` exclusion) gained any new shared foundation to reason from.
- Step 2's `IsRuntimeManagedTcoParamSlot` (`Lowering.cs:2557-2566`) has zero overlap with either the veto
  or the profitability signal: `grep -n "IsRuntimeManagedTcoParamSlot" src/Ashes.Semantics/*.cs` finds it
  used only at `LowerVar` (`Lowering.cs:2595`) and the match-arm join (`Lowering.Patterns.cs:95`) — the
  two sites named in Step 2's own scope, neither of which the veto/profitability call graph reaches from
  either direction. Classifier C (`_runtimeManagedResultTemps`) is consulted by neither
  `IsPermanentlyBlockingTcoSibling` nor `ClassifyTcoParamPromotion`; the profitability signal's whole
  question is answered entirely from classifier A's own resolved-type facts plus `CanArenaReset`/
  `IsResourceHandleType`/`TcoContext.LoopInvariantParams`, none of which Step 2 touched.

**What this means for the actual question Step 3a was asked to re-check.** The third attempt's own
re-diagnosis was "hard to trust" for a reason unrelated to A/B or A/C disagreeing with each other: the
`costSensitive` exception that was later added and then removed (see "Phase 6, profitability-gated veto
relaxation" above) failed on `reverse-complement`'s real `buf` because a strict syntactic membership
test (`TcoContext.AffineStrParams`) could not see past an intervening flush read that the hand-built
unit test's simplified source happened not to reproduce — a **downstream-cost-modeling precision gap**,
not a case of two classifiers silently computing different answers to the same question. The
promotion-cost-signal session's own closing note named the actual missing piece precisely: pricing the
*specific* downstream consumer (loop-entry conversion cost, per-node `RcDup`/`RcDrop` bookkeeping) rather
than only asking whether some sibling blocks the frame at all — and stated plainly that this pricing
"was not chased to a byte-level confirmation" in that session. Steps 1-2 did not touch that downstream
consumer in any way: `GetTcoCopyOutKind`, `CanArenaReset`, `EmitTcoBackEdgeArenaBlock`, and
`TcoBackEdgeRuntimeManagedArgCanReset` are all untouched by either step, and are exactly where that
pricing would have to be built. Since the actual source of "hard to trust" was never the classifier
duplication Steps 1-2 removed, removing it does not make a finer per-parameter rule any more buildable
or trustworthy today than it was when the third attempt's carve-outs were tried and reverted — the
missing analysis is still missing, in the same place, for the same reason.

**Re-running the three named acceptance-gate measurements anyway, since a plausible-sounding argument is
not the same thing as re-measuring.** All three were re-run against the current tree (`0fc99cf`, with no
code changes made this session) as a direct check that nothing about the currently-landed relaxation's
own behavior has drifted, plus the two general sentinels this document tracks for the whole subsystem:

- *Str/closure synthetic case*: the existing `TcoPromotionCostSignalTests.
  Affine_string_accumulator_alongside_a_sometimes_fresh_closure_is_profitable` (part of the full C# run
  below) still asserts `Profitable` for `str`. Additionally compiled and ran, end to end at `-O2`, a
  freestanding program of the same shape (a `Str` accumulator, unconditionally RC-eligible, threaded
  alongside a closure that is a fresh `given`-lambda literal on even steps and threaded unchanged on odd
  ones) for 2,000,000 iterations: correct output (`2000001`, the accumulated string length plus the
  final `fn` result), peak RSS 35,112 KB — small and bounded, in the same range as the 30,976-31,232 KB
  the promotion-cost-signal session measured for its own version of this case, not the ~370 MB scale a
  genuinely blocked promotion produces. Closure/heap mixing still does not trip the veto.
- *`challenges/1brc/brc.ash`*, freshly generated 10,000,000-row and 100,000,000-row `measurements.txt`
  files (station names sampled from the upstream seed list, Gaussian per-station temperatures, same
  generation method `download.sh` already documents): 10M rows — 6,969,712 KB peak RSS; 100M rows —
  9,048,692 KB. Both match §10's (6,970,836 / 9,048,772 KB) and §11's (6,969,920 / 9,048,624 KB)
  previously-recorded numbers within ordinary run-to-run noise (well under 0.1%) — no regression.
- *`challenges/reverse-complement/reverse-complement.ash`*, a freshly generated 1,000,000-sequence FASTA
  input (via the repo's own `fasta` challenge binary): peak RSS 753,912 KB, matching §10's 753,588 KB and
  §11's 753,984 KB within noise — no regression, and nowhere near the ~1,124,352-1,124,864 KB the third
  attempt measured for the fully-removed veto or the ~370 MB delta the `costSensitive` exception's own
  removal was validated against.

Plus the two general sentinels: `fannkuch-redux` N=8 through N=11 — all four constant at the 256 KB
floor (checksums 1616/8629/73196/556355, `Pfannkuchen` 22/30/38/51, matching every prior session's
numbers exactly); `binary-trees` N=21 — 196,096 KB, matching §11's own measurement of the same commit
exactly. A baseline was independently built from a clean `git clone` of this session's own starting
commit (`0fc99cf`, confirmed identical to `origin/main`'s tip) rather than assumed equivalent to the
working tree: `brc`, `reverse-complement`, `fannkuch-redux`, and `binary-trees` all compiled
byte-for-byte identical (`cmp -s`) between the two, as expected given zero source changes were made —
establishing, and exercising, the exact baseline-comparison infrastructure a future session would need
if it does land a change here.

Full validation gate, all against the unmodified tree: `dotnet build Ashes.slnx` clean; C# suite
1722/1722 (0 failed, 0 skipped), including `NestedTcoPatternAliasTests`, `MatchScrutineeWrapperDropTests`,
`OwnershipTests`, `OwnershipProvenanceTests`, `TcoPromotionCostSignalTests`, and `TcoRcEligibilityPredicateTests`
by name (`TcoMixedOwnershipTests` still does not correspond to any class in the tree, exactly as §11
already noted — re-confirmed by `grep -rln TcoMixedOwnership src/`, no output); LSP suite 52/52; e2e suite
(`dotnet run --project src/Ashes.Cli -- test tests`) 544 passed, 0 failed, 44 skipped, matching every
prior session's baseline exactly; `dotnet format Ashes.slnx --verify-no-changes` clean.

**Conclusion: no widening warranted, and none landed.** Steps 1-2's cleanup is real and already
validated (§10, §11), but it addressed a specific, different disease — two independently-coded
predicates silently answering the same eligibility/slot-membership question — that was never the reason
the profitability signal's own finer rule failed on real code. That failure came from a downstream cost
mechanism (loop-entry conversion, per-node runtime-managed bookkeeping) that no session has yet traced
to the instruction level, and Steps 1-2 leave it exactly as untraced as they found it: neither step
touches `GetTcoCopyOutKind`, `CanArenaReset`, `EmitTcoBackEdgeArenaBlock`, or either of
`TcoBackEdgeRuntimeManagedArgCanReset`'s and classifier A's own still-separate list/closure branches.
Building a more precise per-parameter rule than "any (non-closure) blocking sibling demotes the whole
frame" would require exactly the instruction-level downstream-cost tracing the promotion-cost-signal
session's own closing note deferred — a genuine, separately-scoped investigation, not something Steps
1-2's dedup work unlocks as a side effect. Per this document's own standing practice (§9's own framing
for this exact step, and the "Phase 6, third attempt" section's own closing instruction not to reattempt
the same shapes without first building the missing signal), manufacturing a widening here would repeat
exactly the failure mode this subsystem has already been reverted for three times: a change that looks
justified by adjacent cleanup but isn't actually supported by anything that cleanup did. The
currently-landed relaxation (closure/heap-sibling mixing no longer trips the veto; a genuine non-closure
blocking sibling still demotes every promoted parameter in the frame) remains exactly at the scope §11
left it, re-confirmed rather than changed.

**What this narrows for whoever attempts Step 3b.** Step 3a's re-verification directly rules out one
hypothesis a future session might otherwise have had to re-check from scratch: that the classifier
duplication Steps 1-2 removed was secretly entangled with the profitability signal's own known
imprecision. It was not, confirmed by tracing the actual call graph rather than reasoning from the two
areas' shared "classifier" label. Step 3b's real prerequisite is unchanged and unmet: a signal that can
price a specific promotion's actual downstream cost (loop-entry conversion, per-node dup/drop, and, for
an affine-growth accumulator, reservation regrowth) precisely enough to answer "does this promotion pay
for itself" even when a sibling blocks the frame — not the coarser "does any sibling block at all"
question the current signal answers. Nothing found this session makes Step 3b either more or less
tractable than §9.6 already assessed it to be; it narrows what future work does NOT need to re-derive
(the classifier-duplication hypothesis, now checked and ruled out) without narrowing the size of what
still does need building.

## 13. Classifier collapse — Step 3b design: the unified per-parameter TCO ownership record

This is a design-only session, produced against the tree at `fc36a61` (§12's own landing, current
`origin/main` tip at the time this section was written). Per its own brief, it makes **no code
change** anywhere except this section: `git status`/`git diff` are clean against `fc36a61` for every
file except this one, and `dotnet format Ashes.slnx --verify-no-changes` was re-run and stays clean
(a no-op check for a markdown-only change, confirmed rather than assumed). §9.6 scoped Step 3b as "a
real design pass of its own — deciding what a unified per-parameter TCO ownership record looks like
structurally, whether it lives on `TcoContext` or is computed by an extended `FunctionOwnershipSummary`
per §5 item 4 — before any code is written." This section is that design pass.

### 13.1 Re-verifying the two things this section's brief said might have drifted

**`TcoContext`'s field count.** §5 item 4 estimated "~17 independently-scanned classification
fields." Reading `TcoContext` directly (`Lowering.Types.cs:33-143`) finds **31 total fields**, of
which:

- **15 are genuine classification/eligibility data** — the actual target of this step:
  `RuntimeManagedParamSlots`, `RuntimeManagedListParamSlots`, `RuntimeManagedClosureParamSlots`,
  `RuntimeManagedParamTypes`, `LoopInvariantParams`, `FreshRebuiltListParams`, `AffineConsListParams`,
  `ConsumedListTailParams`, `BorrowableConsumedListParams`, `FreshClosureParams`,
  `EscapingDirectPatternBindings`, `AffineStrParams` (12 name/slot-keyed sets or maps), plus
  `ParamNames`/`ParamSlots`/`ParamLabels` (the 3 identity-mapping tables every classifier above indexes
  through by parameter name or slot — not themselves a classification, but load-bearing plumbing every
  one of the 12 depends on).
- **11 are pure codegen bookkeeping**, not ownership classification at all, and out of scope for this
  step regardless of how the unified record is shaped: `RuntimeManagedParamActiveSlots`,
  `RuntimeManagedClosureActiveSlots`, `AffineResvSlots`, `ArenaCursorSlot`, `ArenaEndSlot`,
  `FixedCursorSlot`, `FixedEndSlot`, `CompactionSizeSlot`, `StackPtrSlot`, `CoroutineLoopReset`,
  `OwnershipDepthAtEntry` — these are LLVM-codegen-facing slot numbers and reset-strategy state that a
  unified ownership *decision* would still need to drive, but that are not themselves an ownership
  question and should not be folded into the record proposed below.
- **5 are structural/identity fields unrelated to ownership**: `SelfName`, `BodyLabel`, `ParamCount`,
  `InTailPosition`, `DescendingChain`.

So "~17" underestimated the true count of independently-scanned classification fields by roughly
avoiding double-counting; the corrected number worth citing going forward is **12 name/slot-keyed
classification sets, backed by 3 identity tables** — 15 fields total that a unified per-parameter
record should be able to fully replace, out of `TcoContext`'s 31.

**`LowerLambdaCoreFinalizeTcoOwnership`'s call count.** §9.2 item 5 characterized this as "five calls":
A's refresh → D's resolution → a pending-flag pass → A/B's entry-ownership splice → C's refresh.
Reading it directly (`Lowering.cs:5332-5347`) finds **six calls**, not five:

```csharp
private void LowerLambdaCoreFinalizeTcoOwnership(...)
{
    LowerLambdaCoreRefreshRuntimeManagedTcoParams(tco);       // A (+ profitability, see below)
    ResolvePendingNestedTcoPatternAliasSites(tco);            // D
    ResolvePendingRuntimeArgumentFlags(tco);                  // pending-flag resolution (unrelated to A-D)
    LowerLambdaCoreSpliceTcoEntryOwnership(..., tco, ...);     // A/B entry-ownership splice
    LowerLambdaCoreRecordAccStableFold(lam, tco, specElidedAccs); // NOT an A-D classifier pass (see below)
    LowerLambdaCoreRefreshRuntimeManagedTcoResult(tco, bodyTemp); // C
}
```

The sixth call, `LowerLambdaCoreRecordAccStableFold` (`Lowering.cs:6857`), is address-stable-fold
bookkeeping (a CO-16/CO-32-adjacent question: "does this fold's accumulator come back at a stable
address so an outer loop threading it across a back-edge can keep a plain arena reset") — a real,
separate concern that happens to be sequenced here because it also needs the body fully lowered, but
it is not itself an A/B/C/D ownership classifier and this document's own catalogue (§1, §9.1) never
listed it. This is a correction in the same spirit as §10's redundant-disjunct finding and §11's
"this site doesn't do what the brief assumed" finding: not a functional problem, but anyone designing
around "the five-call sequence" should know it is actually six, one of which is orthogonal. Also worth
recording precisely: `LowerLambdaCoreRefreshRuntimeManagedTcoParams` (`Lowering.cs:5464-5490`) itself
calls `RecordTcoPromotionProfitability` (line 5489) — so the profitability signal already runs
*inside* the "A's refresh" step, not as a separate seventh call; profitability is demoted only later,
inside `LowerLambdaCoreRejectPartialRuntimeManagedTcoFrame` (called from within
`LowerLambdaCoreIdentifyRuntimeManagedTcoParams`, `Lowering.cs:6160`), which itself runs at TWO
different points in the overall sequence: once at loop entry (`Lowering.cs:6107`, unresolved types,
`includeFreshClosures: true`) and once inside the refresh call above (resolved types,
`includeFreshClosures: false`) — the "five/six calls in `FinalizeTcoOwnership`" framing understates how
many total classification passes actually run per loop; the loop-entry pass is a seventh, upstream of
everything `FinalizeTcoOwnership` itself does.

### 13.2 What `FunctionOwnershipSummary` already provides, verified against its actual computation

`FunctionOwnershipSummary` (`OwnershipSummary.cs:49-58`) carries five facts per registered function:
`ParameterOwnership` (Borrowed/Consumed per parameter), `UniqueParameters`, `CapturedValues`,
`ResultReach`/`ResultPoisoned`, `ExpressionFreshness` (per-`Expr`-node, reference-keyed), and
`ResultProvenance` (`RcEligible`/`ForwardsTo`). Three properties of how it is actually computed
(`Lowering.MoveAnalysis.cs`) matter directly for this design:

1. **It is computed once, over the whole desugared program, entirely from AST shape — no `TypeRef`
   is ever consulted.** `AnalyzeReuseCopyElision` (`Lowering.MoveAnalysis.cs:149-201`) registers every
   top-level/`let recursive` function (`RegisterBindings`/`RegisterOneBinding`, `Lowering.MoveAnalysis.cs:219-298`
   — the same `CollectLambdaParams`/`GetInnermostBody` shape `_maFuncs` uses is exactly the shape a TCO
   loop's own recursive binding has), builds a whole-program call-site census
   (`CollectCallsAndEscapes`), then runs `ComputeResultReach` (`Lowering.MoveAnalysis.cs:973-1008`) to a
   monotone least fixpoint. None of this reads a `TypeRef` anywhere — it cannot, because per §2 of this
   document, types are resolved progressively *during* lowering via interleaved unification, and this
   whole analysis is confirmed (by reading its own call site, not assumed) to run once, before any
   function's body is lowered. This is the structural reason it is immune to the exact "classifier D
   reads classifier A before A is populated for this body" timing-gap family (§9.2 item 4, and both
   "Classifier (D) timing gap" sessions) that has twice regressed `fannkuch-redux`: there is no "before
   this body is lowered" moment for a fact computed before *any* body is lowered.
2. **It already flows correctly through a TCO loop's own self-recursion**, not just through ordinary
   fold-style helpers. `ComputeResultReach`'s fixpoint iterates every `_maFuncs` entry
   (`Lowering.MoveAnalysis.cs:973-1008`); when a registered function's own body calls itself, `CallReach`
   (`Lowering.MoveAnalysis.cs:1490-1542`) resolves it via ordinary `CallReachRegistered`
   (`Lowering.MoveAnalysis.cs:1630-1656`), looking up `_maResultReach[name]` — the function's own,
   currently-converging entry. (`_maSelfRecursive`/`CallReachSelfRecursive`,
   `Lowering.MoveAnalysis.cs:1544-1575`, is a *different*, narrower mechanism for the nested
   `let rec go = (given acc -> B) in go` "Map.set shape," not the ordinary top-level
   `let recursive f = given x -> ... f(...) ...` shape a ordinary TCO loop actually uses — verified by
   reading `TryGetNestedRecursiveReturnShape`'s own gate; a plain TCO loop is registered as an ordinary
   `_maFuncs` entry and goes through the general self-referential fixpoint, not this special case.)
   Because `RecordExpressionFreshness` (`Lowering.MoveAnalysis.cs:1248-...`) is invoked from inside
   `ResultReach` for every sub-expression it visits, **a TCO loop's own self-call argument expressions
   are already recorded in `ExpressionFreshness` today** — e.g. `nextPerm`'s own
   `S(perm2)(count2)` construction fed back into its own recursive call is exactly the kind of
   expression this fixpoint already classifies, keyed by AST-node reference identity, fully computed
   before `nextPerm`'s body is ever lowered.
3. **It genuinely cannot answer classifier A's real question on its own**, because classifier A's
   question is fundamentally type-shape-dependent (`IsRcEligibleScalarTupleOrAdtType`,
   `CanRuntimeManageTcoListElement`, `CanArenaReset`, `IsResourceHandleType` — all `TypeRef`-driven,
   `Lowering.cs:6212-6216` and neighbors) and `FunctionOwnershipSummary` is computed before any
   `TypeRef` is resolved. `ExpressionFreshness`/`ParameterOwnership` answer *aliasing/escape* questions
   ("does this expression's value alias any parameter," "is this parameter only read, never
   moved/stored/returned") — necessary ingredients for classifier A's `FreshRebuiltListParams`/
   `ConsumedListTailParams`-style distinctions and for classifier D's escape question, but not a
   substitute for the type-eligibility half of the question.

Two existing, narrower precedents for exactly the kind of structural, fail-closed AST walk a
generalized version of this would need already exist and are worth building from rather than
re-deriving: `ParamUsedOnlyAsBorrowRead` (`Lowering.Borrow.cs:66-117`) — "every occurrence of this
parameter is a positively-verified safe use (here: the resource argument of a whitelisted
borrow-read builtin), else assume the worst" — currently hardcoded to a small `BorrowReadResourceOps`
whitelist (`Lowering.Borrow.cs:26-38`) and resource types only, not a general escape signal; and
`CollectEscapingDirectPatternBindings`/`IsSafeDirectPatternBindingUse`
(`Lowering.Reuse.cs:355-414`, `481-534`) — classifier D's own already-generalized-once "every
occurrence of this pattern-bound name is one of three positively-verified safe shapes (nested match
on the same name; bare unchanged argument at its own parameter's slot in a self-call; bare argument
to a non-constructor call), else it escapes" — which is structurally the same *shape* of analysis as
`ParamUsedOnlyAsBorrowRead`, just already generalized past resources to arbitrary pattern-bound
names, and already scoped to exactly the TCO question at hand. Both are purely syntactic, both are
fail-closed, and both are currently one-off, hand-maintained AST walks local to their own file rather
than facts hosted on `FunctionOwnershipSummary` — this is the concrete gap between "the pattern this
migration wants" and "what exists today."

The shadow-compare infrastructure this design would want to validate against before cutover already
exists and is currently unused for TCO: `Lowering.OwnershipShadowCompare.cs` (75 lines) provides
`TryGetExpressionFreshness`/`ShadowCompareExpressionFreshness`, reusing `ASHES_EXPLAIN_OWNERSHIP` as
its logging channel — wired today only into the Phase 4/5 ADT/List freshness classifiers
(`Lowering.Ownership.cs:2374`, `Lowering.cs:3093,4182,4238` — confirmed by `grep`, no TCO call site
consults it). This is a direct, reusable template for both Step 3b's own validation and Step 4a's
mandated shadow-compare phase (§9.6).

### 13.3 The central design question, answered

**Does the unified record extend `TcoContext`, or is it computed by extending
`FunctionOwnershipSummary`?** Neither alone, and the honest answer is a layering, not a choice:

- The record's **structural ingredients** — which self-call-argument shape a parameter's value takes
  (fresh construction vs. unchanged passthrough vs. consumed-tail-walk vs. mixed/escaping), and
  whether any pattern-extracted sub-binding of it independently escapes — are answerable from AST
  shape alone, need no type information, and are exactly the kind of fact `FunctionOwnershipSummary`
  already computes once, before lowering, immune to timing gaps by construction. These should be
  **read from an extension of `FunctionOwnershipSummary`**, not re-derived by `TcoContext`'s own
  ad hoc walks (today's `Lowering.Reuse.cs` `Collect*` family) — this is the direct application of
  §5 item 4's original vision, and of the exact "safe reuse over a parallel rule" precedent this
  document's own Phase 6 interprocedural-freshness fix already used successfully
  (`IsDirectRcConstruction` falling back to `IsFreshRuntimeManageableAdtExpressionCore` instead of
  re-deriving a parallel rule — §6, "Safe reuse over a parallel rule").
- The record's **type-eligibility and profitability verdict** — is this parameter's resolved type
  RC-manageable at all, does any sibling permanently block the frame — is inescapably a lowering-time
  fact: the parameter's concrete `TypeRef` does not exist until enough of the function's own body has
  been lowered for unification to pin it down (confirmed directly: `LowerLambdaCoreIdentifyRuntimeManagedTcoParams`'s
  loop-entry pass, `Lowering.cs:6107`, sees `TVar`s for an ordinary unannotated parameter; only the
  post-body `LowerLambdaCoreRefreshRuntimeManagedTcoParams` pass, `Lowering.cs:5476-5479`, sees
  resolved types — this is not an accident of today's implementation, it is a direct consequence of
  §2's "ownership decisions are made at lowering time, interleaved with unification" architecture, and
  no amount of restructuring the *classifier* code changes *when types resolve*). This half **must
  remain a `TcoContext`-resident fact**, computed at lowering time, because there is no earlier point
  in the pipeline where it could be computed instead.

Concretely: **materialize the unified per-parameter record on `TcoContext`** (it cannot live purely on
`FunctionOwnershipSummary`, because half its fields need information that does not exist until that
specific loop's own body is lowered) **but populate its structural fields by reading an extended
`FunctionOwnershipSummary` for the enclosing self-recursive function**, instead of by running
`TcoContext`'s own separate `Collect*` AST walks a second time over the same body. This directly
targets the disease this whole document is about — "two classifiers, same question, different code
paths, can disagree" — for the TCO/whole-program pair specifically: today, `FunctionOwnershipSummary`
already computes (for every function, TCO loops included) facts adjacent to what `FreshRebuiltListParams`/
`ConsumedListTailParams`/`EscapingDirectPatternBindings` re-derive independently in
`Lowering.Reuse.cs`; a second independent derivation of a materially similar question is exactly the
pattern Step 1 (A/B) and the "safe reuse over a parallel rule" fix (Phase 6) each already closed for a
different pair, not something to reproduce a third time here by accident.

```mermaid
flowchart TB
    subgraph pre["Pre-lowering, whole-program, AST-only (once, before any body lowers)"]
        AST["Desugared program AST"]
        AST --> FOS["FunctionOwnershipSummary\n(extended, §13.4)"]
        FOS --> SHAPE["Per (function, param):\nSelfCallArgumentShape\n+ escape verdict"]
    end
    subgraph perloop["Per TCO loop, at lowering time (types resolve progressively)"]
        ENTRY["Loop-entry pass\n(provisional: types still TVars)"]
        REFRESH["Post-body refresh pass\n(authoritative: types resolved)"]
        SHAPE --> ENTRY
        SHAPE --> REFRESH
        ENTRY --> RECORD["TcoContext.ParamOwnership\nDictionary<int, TcoParamOwnership>"]
        REFRESH --> RECORD
    end
    RECORD --> CONSUMERS["~50 consumer call sites\n(LowerVar, match-join, exit-drop,\nback-edge normalization, ...)"]
```

### 13.4 Concrete shapes

**Extend `FunctionOwnershipSummary` with one new field**, computed by the same `ComputeResultReach`
fixpoint pass that already computes `ExpressionFreshness` (`Lowering.MoveAnalysis.cs:1017-1033`), so it
is populated with zero additional passes over the AST — only additional bookkeeping inside the walk
that already visits every self-call:

```csharp
// OwnershipSummary.cs
internal enum TcoSelfCallArgumentShape
{
    /// Every self-recursive call site's argument at this parameter's position is a syntactically
    /// fresh construction (ExpressionFreshness[argExpr] == true for every occurrence) — the
    /// FreshRebuiltListParams/FreshClosureParams shape.
    FreshRebuilt,

    /// Every self-recursive call site's argument at this parameter's position is a bare reference
    /// to this same parameter, unchanged — the LoopInvariantParams shape.
    UnchangedPassthrough,

    /// Every self-recursive call site's argument at this parameter's position is derived from this
    /// same parameter's own value by a structural, strictly-smaller decomposition (a cons-tail, a
    /// single field extraction) that is never rebuilt, never embedded in a constructed value, and
    /// never handed to a different parameter's slot — the ConsumedListTailParams shape.
    ConsumedTail,

    /// No single shape above holds at every self-recursive call site, or the argument aliases a
    /// DIFFERENT parameter, or the function has no self-recursive call sites at all (ordinary
    /// non-TCO function). Conservative fallback — carries no promotion opportunity by itself.
    Mixed,
}

// One entry per (function, parameter name) — a TCO loop's own accumulator parameters only;
// absent for a parameter with no self-recursive call sites to classify (Mixed is the explicit
// "classified but inconclusive" case; a missing key is "never asked," e.g. an ordinary
// non-recursive function's own parameters).
internal sealed record TcoParamStructuralFacts(
    TcoSelfCallArgumentShape Shape,
    bool EscapesIndependently); // classifier D's verdict — does ANY pattern-derived sub-binding
                                // of this parameter's value, within its own recursive body, have
                                // an occurrence that is not one of CollectEscapingDirectPatternBindings's
                                // three already-known-safe shapes (generalized as a first-class
                                // per-parameter fact instead of a side pending-table)

internal sealed record FunctionOwnershipSummary(
    string Function,
    IReadOnlyList<string> Parameters,
    IReadOnlyDictionary<string, ParameterOwnership> ParameterOwnership,
    IReadOnlySet<string> UniqueParameters,
    IReadOnlyList<string> CapturedValues,
    IReadOnlyDictionary<string, int> ResultReach,
    bool ResultPoisoned,
    IReadOnlyDictionary<Expr, bool> ExpressionFreshness,
    FunctionResultProvenance ResultProvenance,
    IReadOnlyDictionary<string, TcoParamStructuralFacts> TcoParamFacts); // NEW
```

**`TcoContext` collapses its 12 name/slot-keyed classification fields (§13.1) to one dictionary**,
keyed by slot (matching every existing consumer's own preferred key — most read by slot, a few by
name via `ParamNames`/`ParamSlots`, which stay as the identity-mapping plumbing they already are):

```csharp
// Lowering.Types.cs — TcoContext
public Dictionary<int, TcoParamOwnership> ParamOwnership { get; } = [];
// Replaces: RuntimeManagedParamSlots, RuntimeManagedListParamSlots, RuntimeManagedClosureParamSlots,
// RuntimeManagedParamTypes, LoopInvariantParams, FreshRebuiltListParams, AffineConsListParams,
// ConsumedListTailParams, BorrowableConsumedListParams, FreshClosureParams,
// EscapingDirectPatternBindings, AffineStrParams. RuntimeManagedParamActiveSlots/
// RuntimeManagedClosureActiveSlots/AffineResvSlots (pure codegen slot bookkeeping, §13.1) are
// unaffected and stay exactly as they are.
```

```csharp
// New file, e.g. Lowering.TcoOwnership.cs
internal enum TcoParamEligibility
{
    /// Type shape alone (IsRcEligibleScalarTupleOrAdtType / list-element shape) does not license
    /// RC representation regardless of self-call argument shape — e.g. a multi-constructor ADT
    /// with pointer-bearing fields, or a List(Str) whose element itself needs RC management.
    IneligibleByType,

    /// Type shape licenses RC representation, but the self-call argument shape does not license
    /// the specific normalization strategy that type shape needs (e.g. a List eligible only via
    /// ConsumedTail, but this parameter's own self-calls disagree about which structural
    /// sub-decomposition to use across call sites — TcoSelfCallArgumentShape.Mixed).
    IneligibleByShape,

    /// Both type shape and self-call argument shape license RC representation, independent of any
    /// sibling parameter in the same frame (classifier A's own question, considered alone).
    IndependentlyEligible,
}

internal sealed record TcoParamOwnership(
    string ParamName,
    int Slot,
    TypeRef ResolvedType,
    TcoParamEligibility Eligibility,
    TcoPromotionVerdict Profitability,  // Lowering.TcoPromotionCostSignal.cs, computed inline (§13.5)
    bool EscapesIndependently)          // sourced from FunctionOwnershipSummary.TcoParamFacts (§13.2/13.4)
{
    /// The actual representation decision — the one fact every one of the ~50 downstream call
    /// sites should read, instead of testing RuntimeManagedParamSlots.Contains(slot) or combining
    /// several of TcoContext's old name/slot sets by hand.
    public bool RuntimeManaged =>
        Eligibility == TcoParamEligibility.IndependentlyEligible
        && Profitability == TcoPromotionVerdict.Profitable;
}
```

**Consumer call sites change from set-membership tests to one field read.** For example,
`IsRuntimeManagedTcoParamSlot` (`Lowering.cs:2566-2567`, Step 2's own shared extraction) becomes:

```csharp
private bool IsRuntimeManagedTcoParamSlot(Binding.Local local) =>
    _tcoCtx?.ParamOwnership.TryGetValue(local.Slot, out var ownership) == true && ownership.RuntimeManaged;
```

with the same one-line shape at every other of the ~50 sites the audit catalogued (§4) that today read
`tco.RuntimeManagedParamSlots.Contains(slot)`, `tco.RuntimeManagedListParamSlots.Contains(slot)`, or
`tco.RuntimeManagedClosureParamSlots.Contains(slot)` — each becomes `.ParamOwnership[slot].RuntimeManaged`
plus, where a caller specifically needs to know *why* (e.g. `LowerLambdaCoreRefreshRuntimeManagedTcoParams`'s
own closure-vs-non-closure branch, `Lowering.cs:5482`), a read of `.ResolvedType`/`.Eligibility` instead
of a separate `RuntimeManagedListParamSlots`/`RuntimeManagedClosureParamSlots` set membership test.
Classifier B (`GetTcoCopyOutKind`/`TcoBackEdge*`) is **not** folded into this record — per §9.4's
already-confirmed finding (re-verified here, unchanged: every predicate in that family still depends
only on `TypeRef`/`TypeSymbol` shape, never on any per-value ownership fact), it stays a pure,
stateless, downstream-only query, *consulted* by whatever build the record but not a field of it.
Classifier C (`_runtimeManagedResultTemps`) also stays separate, for a different, orthogonal reason:
it answers a strictly finer-grained question ("is this specific already-lowered IR *temp*, right now,
runtime-managed") that a per-parameter record cannot precompute, because a temp's value may flow
through arbitrary intervening expressions the parameter-level record has no visibility into — C's own
job is to propagate the parameter-level verdict forward through IR, not to duplicate it.

### 13.5 Re-expressing the veto

§9.6's own framing for Step 3b: "the veto would need to be re-expressed as 'no per-parameter clearing
at all, only the profitability-gated demotion already landed.'" This is achievable, concretely, as
follows: `Profitability` becomes a field computed **once, at record-construction time**, by asking
`ClassifyTcoParamPromotion`'s existing question (`Lowering.TcoPromotionCostSignal.cs:168-187`, itself
unchanged) — but as part of building every parameter's record in one pass, not as a later mutation
applied to an already-populated `RuntimeManagedParamSlots` set. Concretely, `BuildTcoParamOwnership`
replaces `LowerLambdaCoreIdentifyRuntimeManagedTcoParams` + `RecordTcoPromotionProfitability` +
`LowerLambdaCoreRejectPartialRuntimeManagedTcoFrame` + `DemoteUnprofitableRuntimeManagedTcoParams` +
`ClearOneRuntimeManagedTcoParam` (five functions, `Lowering.cs:6118-6291`) with one two-phase function:

```csharp
private void BuildTcoParamOwnership(IReadOnlyDictionary<string, Binding> scope, TcoContext tco)
{
    // Phase 1: every parameter's OWN eligibility, independent of siblings (classifier A's exact
    // question, unchanged — IsIndependentlyRcEligibleTcoParam's logic, reading TcoParamStructuralFacts
    // off the FunctionOwnershipSummary for tco.SelfName instead of tco's own now-removed
    // FreshRebuiltListParams/AffineConsListParams/ConsumedListTailParams/FreshClosureParams sets).
    var eligibility = new Dictionary<int, (TypeRef Type, TcoParamEligibility Eligibility)>();
    foreach (int slot in tco.ParamSlots) { /* ... */ }

    // Phase 2: profitability, reading every OTHER slot's already-computed Phase-1 eligibility
    // (IsPermanentlyBlockingTcoSibling's exact question, unchanged) — this is the one place a
    // parameter's own verdict depends on a sibling's, so it must run as a genuinely second pass
    // over the same already-resolved set, not interleaved with Phase 1.
    foreach (int slot in tco.ParamSlots)
    {
        // ... EscapesIndependently sourced from FunctionOwnershipSummary.TcoParamFacts[tco.SelfName][name]
        tco.ParamOwnership[slot] = new TcoParamOwnership(name, slot, type, elig, profitability, escapes);
    }
}
```

`RuntimeManaged` is then a **derived, read-only property** (§13.4) — there is no `Clear`/`Demote`
mutation anywhere in the design, which is a real structural difference from today's code (where
`ClearOneRuntimeManagedTcoParam` mutates six different `TcoContext` sets after the fact) and matches
§9.6's framing precisely.

**One honest limit this re-expression does not remove**: the fundamental two-phase *temporal*
structure (a provisional loop-entry pass with unresolved types, then an authoritative post-body
refresh pass with resolved types) survives regardless of how the record is shaped, because — per
§13.3 — it is forced by *when types resolve*, not by how the classifier code is organized. What this
design does collapse is the **five/six separately-timed, separately-combined passes within each of
those two moments** into one coherent `BuildTcoParamOwnership` call at loop-entry (provisional) and
one at refresh (authoritative) — not the two-moment structure itself. A future implementer should not
expect to remove the loop-entry-vs-refresh split; only the internal fragmentation *within* each of
those two moments is what this record actually eliminates.

### 13.6 Part 2 — sequencing versus Step 4, and the fork that needs a decision

**If Step 3b is built as a pure repackaging** — keep `TcoContext`'s own `Collect*` AST walks exactly
as they are today, and merely gather their existing outputs into one `TcoParamOwnership` record
instead of leaving them as separate sets — **it does not advance Step 4 at all.** It is a real,
useful cleanup (one query instead of a scattered OR at ~50 call sites, and a mechanical, `git
blame`-able reduction in `TcoContext`'s own surface area), but classifier D's own escape question
would still be answered by the exact same `CollectEscapingDirectPatternBindings` mechanism, at the
exact same point in the pipeline, with the exact same twice-regressed fragility (§9.5) — the record
would just be a new container the same old fact gets copied into.

**If Step 3b is built by actually routing `TcoSelfCallArgumentShape`/`EscapesIndependently` through an
extended `FunctionOwnershipSummary`** (§13.4) instead of leaving `Lowering.Reuse.cs`'s own walks in
place, it **does make Step 4 more tractable**, for a reason grounded in what this session actually
found rather than by analogy: doing so requires generalizing exactly the fail-closed,
"every-occurrence-is-a-positively-verified-safe-shape" structural walk that `CollectEscapingDirectPatternBindings`/
`IsSafeDirectPatternBindingUse` (§13.2) already is, from "a TCO-parameter-local pending table,
consulted only by `ResolvePendingNestedTcoPatternAliasSites`" into "a first-class,
pre-lowering-computed fact on `FunctionOwnershipSummary`, consulted the same way `ExpressionFreshness`
already is." That is not a new liveness mechanism invented for Step 4's sake — it is Step 4's own
prerequisite (§9.6: "a real liveness signal... does this specific pattern-extracted binding's own live
range end before the iteration completes") being built one step early, under Step 3b's own name,
while its scope is still bounded to the one classifier (A, via the self-call-argument-shape question)
that has never regressed from an over-generalization the way D's escape check has (twice). Building it
first against A — the classifier this document's own history shows is comparatively well-behaved — and
then handing the *same* mechanism to D in Step 4, rather than building it for the first time directly
against D, is the concrete way "3b makes 4 more tractable" cashes out: 4a's shadow-compare phase would
be comparing D's existing syntactic enumeration against a mechanism *already soaked* on A's real
production traffic (the full `challenges/` suite this document already re-runs at every step), not
against a brand-new, unvalidated analysis making its first contact with real code on the single most
historically fragile classifier in the compiler.

**The genuine fork.** These two versions of Step 3b are not "the same step, done more or less
carefully" — they are different-sized undertakings with different risk profiles, and picking between
them is a scope decision the project owner should make explicitly rather than have it default to
whichever a future session finds easier to start:

- **Option A — repackaging.** Build `TcoParamOwnership` as a container over today's existing
  `Collect*`-derived facts, unchanged. Genuinely close to single-session (it is a mechanical
  reshaping of already-correct data into one dictionary, plus rewiring ~50 call sites to read one
  field instead of testing several sets — real effort, but the *classification logic itself* does not
  change, so the validation bar is "byte-identical compiled output everywhere," the same bar Steps
  1-2 already cleared). Reduces `TcoContext`'s surface area and gives every future classifier-D
  attempt (Step 4) one clean place to plug its eventual verdict into, but does not itself make Step 4
  easier or safer to build.
- **Option B — re-derivation through `FunctionOwnershipSummary`.** Additionally re-derive classifier
  A's `FreshRebuiltListParams`/`AffineConsListParams`/`ConsumedListTailParams`/`FreshClosureParams`
  logic (and, per the argument above, generalize `CollectEscapingDirectPatternBindings` into a
  `FunctionOwnershipSummary`-hosted fact) from the AST-only fixpoint instead of `Lowering.Reuse.cs`'s
  own separate walks. This is comparably sized to Phase 4/5 (each of *four* name-sets needs the same
  exhaustive, category-by-category "do the old and new derivations actually agree" verification Step 1
  needed for classifier A/B's overlap, §10) **plus** the `FunctionOwnershipSummary` extension itself
  plus a shadow-compare soak (reusing §13.2's existing infrastructure) before cutover — realistically a
  multi-session effort in its own right, not "Step 3b" as a single unit. It is the option that actually
  fulfills §5 item 4's original vision and the one that pays forward into Step 4, but §9.6's own
  "Step 3 is explicitly not single-session" caveat applies to it with more force than to Option A.

Neither option is wrong; they trade off differently against this document's own repeatedly-stated
priority ("a missed optimization is always acceptable, a regression or an unlanded multi-session
effort is not automatically a failure — the honest bottom line matters more than a small-sounding
first step"). Building Option A first and stopping there would be a legitimate, safe, landable slice
that leaves Step 4 exactly as hard as it is today; building toward Option B is the only path that
uses this design's own central finding (§13.3) to actually shrink Step 4's remaining work, but should
not be scoped, budgeted, or promised as fitting in the same session as Option A's mechanical part.
**Recommendation for whoever picks this up next**: do Option A's mechanical record-and-rewire first,
on its own, validated exactly as Steps 1-2 were (byte-identical compiled output against the full
`challenges/` suite) — it is real, safe, immediately landable value and de-risks the ~50-call-site
rewiring independent of anything else. Then treat Option B's `FunctionOwnershipSummary` extension as
its own, separately-scoped, separately-budgeted follow-up (plausibly one category at a time, the same
cadence Phase 4/5 used for ADT/Tuple/List) — explicitly the prerequisite this section's own evidence
says Step 4 needs, not a detour from it. Do not attempt Option B and Step 4 in the same session
regardless of how Option B goes: per §9.5, D's own history (two regressions from two separately
scoped, separately validated, full-suite-passing sessions) means Step 4 needs its own mandatory 4a
shadow-compare soak (§9.6) no matter how solid Option B's foundation turns out to be.

### 13.7 What remains open

This section is a design, not an implementation, and per its own brief no code was written. What a
future session would still need to settle before writing `BuildTcoParamOwnership` for real: the exact
enumeration rule for `TcoSelfCallArgumentShape` (this section sketches the four cases at the level
`FreshRebuiltListParams`/`ConsumedListTailParams`/etc. already draw them, but the precise AST-walk
logic inside `ComputeResultReach`/`CallReach` needed to classify "which shape does self-call argument
expression `E` at parameter slot `i` exhibit" was not written here, only motivated); whether
`BorrowableConsumedListParams`'s own narrower "inspect-only" sub-case is a fifth `TcoSelfCallArgumentShape`
value or a separate boolean orthogonal to shape (this section did not trace `IsBorrowableInspectOnlyList`
closely enough to decide); and the exact rewiring diff for each of the ~50 consumer call sites §4
catalogued (this section sketches the pattern via `IsRuntimeManagedTcoParamSlot` but does not enumerate
all fifty). None of these change this section's central recommendation (§13.3) or its sequencing
assessment (§13.6); they are the concrete work Option A/B would each still need to do.
