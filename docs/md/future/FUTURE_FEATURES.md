# Future Features

Planned features and future work for the Ashes language and ecosystem. **Shipped** features are
documented in the normative docs under [`docs/md/`](../index.md) — syntax/semantics in
[Language Spec](../reference/language.md), library APIs in [Standard Library](../reference/standard-library.md),
runtime/backend behavior in [Architecture](../internals/architecture.md), and the history of the
compiler's optimization/codegen work in the [Compiler Changelog](../internals/changelog.md) — not here.

| Feature | Status | Description |
|---------|--------|-------------|
| [Self-Hosting](SELF_HOSTING.md) | Exploratory | Rewrite the compiler in Ashes |
| [WebAssembly Target](WASM_TARGET.md) | Exploratory | A `wasm32` backend for browsers and sandboxed plugin hosts |
| [Package Registry Website: Follow-up Work](REGISTRY_WEBSITE.md) | Deferred product work | Follow-up work for the shipped public package-discovery website |
| [Compiler Parity Fuzzing](COMPILER_PARITY_FUZZING.md) | Proposed | Differentially fuzz the stage-0 and self-hosted compilers against each other |
| [Inferred Borrow Lifetimes for Ordinary Values](INFERRED_BORROW_LIFETIMES.md) | Research proposal | Investigate richer borrow-lifetime inference to remove provably unnecessary RC operations |
| [Optimizer Pass Observability](OPTIMIZER_PASS_OBSERVABILITY.md) | Proposed | Optional per-pass instrumentation to see which optimizer pass changed what |

---

## Ground Rules

1. **Spec first.** Update the [Language Reference](../reference/language.md) before
   implementing any new syntax or semantic rule.
2. **Layer discipline.** Respect the project dependency graph
   (Frontend → Semantics → Backend). Runtime behaviour never goes in
   Frontend.
3. **Test every invariant.** Each feature must ship with tests that prove
   the new guarantees.
4. **No user-visible `Drop`.** `Drop` is a compiler concept. Users see
   automatic cleanup.
5. **Purity preserved.** All values are immutable. There is no mutation.
   All APIs — standard library and user-defined — are pure: they return
   new values and never modify their arguments. There are no in-place
   updates visible to user code.
6. **No tracing GC.** Ordinary lifetime operations are compiler-inserted;
   resource cleanup remains statically verified.

---

## Remaining Optimizer Opportunities

The 2026-08 optimizer audit shipped its backlog; what it built and measured is recorded in the
[Compiler Changelog](../internals/changelog.md), the current pipeline in
[Architecture](../internals/architecture.md#ir-optimizer), and the self-hosting deltas in
[Self-Hosting](SELF_HOSTING.md). These are the pieces it deliberately left open, with the finding that
should gate any future attempt so it is not re-derived:

- **Match compilation beyond one grouping level.** Arms are grouped by outer constructor tag with one
  shared tag test per group; a multi-case group still re-tests the outer tag per case, and there is no
  column reordering or guard interaction within a group. Any dead-arm elimination must use the sound
  recursive coverage engine (`TryGetMissingPatternCore`, the one behind the "missing case" diagnostic),
  never top-level tag coverage — tag coverage was tried twice and is unsound for nested sub-patterns
  (`Ok(true) | Ok(false) | Error(_)`) — and must unify each guard-free pattern's type with the scrutinee
  first, because a parameter's type is routinely still unresolved when the match is lowered.
- **Tail contification of local helpers.** Deferred. `TcoContext` is the backbone of the enclosing
  function's per-parameter RC/arena representation decisions, so a second join point per contified
  helper means a second instance of that machinery inside one frame. The motivating case also
  disappeared: once a real read-builtin RC leak found during baselining was fixed, a local helper and
  its hand-inlined form measured identically at `-O0` and `-O2`. Find a program where they differ
  before implementing.
- **Multi-anchor Perceus drop placement.** Implemented, then reverted on evidence. Placing one drop for
  a value with several lexical anchors needs a union of reachable regions that is unsound across a
  TCO back-edge (a `HasBackEdge` guard fixes the segfault), and after the fix every real and synthetic
  program produced byte-identical IR: lowering already places each branch's drop at that branch's true
  last use. Revisit only with a program where a lexical anchor provably sits later than the branch's
  real last use.
- **Closure devirtualization to a small label set.** A call whose closure can be one of 2-4 known
  labels could dispatch directly per arm (lambda-set specialization); today only a single agreeing
  label devirtualizes.
- **Closures with three or more scalar captures.** One capture rides the `env` word and a second the
  otherwise-unused ownership-flag word of the shared three-word call signature; a third needs a
  direct-call-only worker with a per-function parameter list. A worker for a unary function alone is
  what LLVM's dead-argument elimination already does at `-O2`, so build the convention only together
  with a measured consumer (N>=3 scalarization, uncurrying, or contification of curried helpers).
- **Open-world reuse past a statically-resolved callee.** In-place reuse borrowing now survives a
  hand-off to a callee proven inspect-only by a whole-program fixpoint; a higher-order or unresolved
  callee still forces the defensive copy.
- **Mutual-recursion merging for slot types without a default.** Groups whose differing parameter
  types are user-declared, tuples, functions, or unresolved keep the closure path; merging them needs
  the loop's per-parameter active-flag machinery to tolerate an uninitialized inactive slot rather than
  a default literal.
- **Heap-closure local helpers.** A let-bound helper that also escapes keeps its runtime-managed
  closure and `RcDrop`; only stack closures lose their environment through scalarization.
- **Interprocedural summary unification.** Only the fixpoint skeleton (`WholeProgramFixpoint`) is
  shared; `FunctionOwnershipSummary` (AST-phase, `FuncKey`-keyed) and the IR-phase label-keyed
  analyses stay separate by design, since forcing one node type across that phase boundary buys
  nothing their consumers need.
- **The per-call arena window over a non-tail recursive producer.** `LowerCallGeneral` opens an arena
  window around every general call, and `LowerCallRestoreArena` copies an arena-placed result out of
  it before reclaiming. For a self-recursive producer — `let recursive makeList (count: Int) = if
  count == 0 then [] else 7 :: makeList(count - 1)` — that is a `CopyOutList` of a result that grows
  with the recursion level, so peak memory is quadratic in the list's length. Measured (linux-x64,
  peak RSS): 70 MB at N=2000, 259 MB at 4000, 1010 MB at 8000, against 320 KB of actual data at
  N=20000. The copy is not avoidable while the window is: the restore rewinds the cursor to just
  below the returned cells, so the cons cell the caller allocates next lands on top of them.
  Confirmed by deleting the copy and the teardown together, which takes N=8000 from 1010 MB to
  4.4 MB with the answer unchanged.
  Do not attempt the obvious fix without a repro harness wider than the test suites. Skipping the
  window for a call to a member of the enclosing recursive group was tried four ways — leaving the
  window open, suppressing the `SaveArenaState` entirely, restricting to list results, restricting to
  a call outside any TCO context, and restricting to the lexically enclosing function's own label —
  and every one of them passes the whole stage-0 gate (2553 unit, 72 LSP, 748 end-to-end tests) while
  segfaulting the self-hosted semantics test binary at the same point, jumping through an overwritten
  code pointer. Treating the un-copied result like a stable reuse result in
  `resultCopySeversArgumentReferences` and `resultCopyCopiesElements` does not change that. The real
  fix has to keep the window and promote the result out of the reclaimed range rather than copy it,
  and the shape that breaks the naive version has to be minimized first — `selfhost/tests/semantics`
  is currently the only thing that catches it, which is itself a coverage gap worth closing.
