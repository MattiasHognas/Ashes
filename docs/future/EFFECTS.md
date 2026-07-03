# Algebraic Effects & Handlers — Status & Roadmap

**Status:** Stages 1 and 2 are implemented (`LANGUAGE_SPEC.md` section 20 is the authoritative
surface); Stage 3 (one-shot resumptive handlers) is not started. See "Implementation notes"
below for how Stage 2 represents handler evidence and the current cross-feature limitations.

Algebraic effects let a function *declare* the operations it needs — `now`, `log`,
`lookup`, `random`, `readFile` — without deciding what they **mean**. The caller chooses the
meaning by installing a **handler**. The same business code runs against a real handler in
production and an injected handler in tests, with no parameter threading, no mocking framework, and
no monad-transformer plumbing.

## Why "Effects", not "IO Types"

This row was historically named *Effects / IO Types* because the two were alternatives: either a
general effect system **or** narrowly-typed IO functions. We are building the general effect system,
so the narrow name is misleading — **effects are not limited to IO.** A handler can interpret an
operation as console IO, but equally as a frozen clock, a captured log buffer, a fixed price table,
a deterministic RNG, a retry policy, or an early exit. IO is one handler among many.

## What it buys — dependency injection without a framework

The headline use is **deterministic dependency injection**, which is where the testability payoff
lives. A function performs an abstract operation; the handler swaps the implementation:

- **Capabilities:** `Clock`, `Random`, `Env`, `FileSystem`, `Log` — real in production, fake/fixed
  in tests. No `Clock`/`Logger` parameter polluting every signature.
- **Typed, resumable error handling:** a handler can `resume` with a default, retry, or abort — a
  superset of `Result` when the *handler* should decide recovery.
- **Async:** `await` is one specific effect handler; effects generalize the machinery Ashes already
  has for `async`/`await`.
- **Local state, generators, parsers:** expressible with handlers (subject to the continuation
  limits below).

## Design at a glance (settled decisions)

| Decision | Choice | Rationale |
|---|---|---|
| Mechanism | Lexical **handlers** (perform / handle / resume) | Ashes has no typeclasses and no module functors, so the Haskell-`mtl` and ML-functor injection routes are unavailable; lexical handlers are the natural fit |
| Effect typing | **Typed** effect rows, inferred (Koka/Frank-style) | Unlike OCaml 5, an unhandled effect is a *compile-time* error, not a runtime crash — suits a no-runtime language |
| Row notation | `uses { ... }` | `!` reads as "not"; `<>` clashes with comparison; `uses` has no symbol collision and reads in English |
| `perform` keyword | **Optional** | `perform Clock.now(x)` and `Clock.now(x)` are identical programs; the keyword is a greppability marker, the row in the type is the source of truth |
| Operation signatures | **Optional**, inferred | Inferred locally from perform-sites + handler arms; required only at module boundaries or for intentionally-polymorphic operations |
| Handler types | **Inferred**, row-polymorphic | A handler discharges exactly the effects it has arms for and is transparent to the rest (`{A,B | e}`) |
| Written rows | **Closed by default** | A bare `{A, B}` means *exactly* A and B; add a tail `{A, B | e}` to opt into passthrough. Inference always produces the open form |
| Continuations | **One-shot / tail-resumptive only** | Multi-shot needs GC-style continuation copying, which collides with Ground Rule 6 (no GC) and the affine-ownership / in-place-reuse work; one-shot is consumed exactly once and fits affine ownership |
| Multi-shot | **Out of scope** | Deferred indefinitely; documented limitation, not a TODO |
| Built-in IO | **Default top-level handler** | `Ashes.IO.print` works in production with no explicit `handle`; tests override |

## Surface syntax

### Effect declarations

An effect is a named set of operations, declared like a `type`:

```
effect Clock =
    | now : Unit -> Int          // explicit operation signature

effect Log =
    | log                        // implicit: signature inferred from uses + handler arms

effect State(a) =                // effect type parameter, for polymorphic operations
    | get : Unit -> a
    | set : a -> Unit
```

Operation signatures are optional. A bare `| lookup` is valid in a single compilation unit; the type
is inferred by unifying every perform-site and every handler arm, then generalized like a `let`.
Explicit signatures are required only when (1) the effect is exported from a module — its operations
are then a published interface that cannot be inferred across separate compilation — or (2) an
operation is intentionally polymorphic, in which case it needs a type variable, usually via an
effect type parameter as in `State(a)` above.

### Performing an operation

```
let t = perform Clock.now(Unit)      // explicit form
let t = Clock.now(Unit)              // implicit form — identical program
```

`perform` is an optional keyword. Operations are always qualified by their effect (`Clock.now`) so
no ambiguity arises when two effects share an operation name.

### Effect rows in signatures

```
let taxFor  : Int -> Int                       = ...   // pure: no row
let priceOf : Str -> Int uses {Prices}         = ...   // performs one effect
let run     : ... uses {Prices, Clock | e}     = ...   // open row: passes other effects through
```

A function with no `uses` clause is pure. A bare `uses {A, B}` is **closed** (exactly A and B); a
trailing row variable `uses {A, B | e}` is **open** (at least A and B). Type inference produces the
open form; a written closed row is a deliberate restriction.

### Handlers

```
handle work(Unit) with
    | Clock.now(_)  -> resume(realClock(Unit))   // operation arm: args + a one-shot `resume`
    | Log.log(msg)  -> let _ = emit(msg) in resume(Unit)
    | return(r)     -> r                          // runs on the computation's final value
```

A handler installs an interpretation over a scope. Each operation arm receives the operation's
arguments and a one-shot continuation `resume`; calling `resume(v)` returns `v` to the perform-site
and continues. The `return` arm transforms the computation's final value. A handler **discharges
exactly the operations it lists** and is transparent to any other effects, which is why its inferred
type is row-polymorphic.

## Worked example — production

This is shown twice as two **complete, self-contained programs**: a maximally-explicit version and a
genuinely-implicit one. They must compile to the same inferred types and the same lowered IR and
print the same output — that equivalence is the conformance check for the optional-`perform` and
optional-annotation decisions. Only effect and type *declarations* survive into the implicit version
(an effect's existence and a record's shape can't be inferred); everything else — `perform`,
operation signatures, function signatures, `uses` rows, and the handler's type — is dropped.

### Explicit — every annotation and `perform` written out

```
effect Prices =
    | lookup : Str -> Int        // operation signature given
effect Clock =
    | now : Unit -> Int
effect Log =
    | log : Str -> Unit

type Receipt =
    | item: Str
    | base: Int
    | tax: Int
    | total: Int
    | stamp: Int

let taxFor : Int -> Int =                                // pure: no effect row
    fun cents -> cents / 10

let priceOf : Str -> Int uses {Prices} =                 // effect row written
    fun item -> perform Prices.lookup(item)

let processOrder : Str -> Receipt uses {Prices, Clock, Log} =
    fun item ->
        let _ = perform Log.log("processing " + item)
        in
            let base = perform Prices.lookup(item)
            in
                let tax = taxFor(base)
                in
                    let total = base + tax
                    in
                        let _ = perform Log.log("total " + Ashes.Text.fromInt(total))
                        in
                            let t = perform Clock.now(Unit)
                            in Receipt(item = item, base = base, tax = tax,
                                       total = total, stamp = t)

let runProduction : (Unit -> Receipt uses {Prices, Clock, Log}) -> Receipt =
    fun work ->
        handle work(Unit) with
            | Prices.lookup(item) -> resume(Ashes.Catalog.priceCents(item))
            | Clock.now(_)        -> resume(Ashes.Time.unixSeconds(Unit))
            | Log.log(msg) ->
                let _ = Ashes.IO.writeLine("[log] " + msg)
                in resume(Unit)
            | return(r) -> r

let receipt = runProduction(fun _ -> processOrder("widget"))
in Ashes.IO.print(receipt.item + " total=" + Ashes.Text.fromInt(receipt.total))
```

### Implicit — no `perform`, no operation/function signatures, no rows

```
effect Prices = | lookup         // operation signature inferred from uses + handler arm
effect Clock  = | now
effect Log    = | log

type Receipt =
    | item: Str
    | base: Int
    | tax: Int
    | total: Int
    | stamp: Int

let taxFor  = fun cents -> cents / 10        // : Int -> Int            inferred
let priceOf = fun item -> Prices.lookup(item) // : Str -> Int uses {Prices}  inferred

let processOrder = fun item ->               // : Str -> Receipt uses {Prices, Clock, Log} inferred
    let _ = Log.log("processing " + item)
    in
        let base = priceOf(item)
        in
            let tax = taxFor(base)
            in
                let total = base + tax
                in
                    let _ = Log.log("total " + Ashes.Text.fromInt(total))
                    in
                        let t = Clock.now(Unit)
                        in Receipt(item = item, base = base, tax = tax, total = total, stamp = t)

let runProduction = fun work ->              // handler type inferred, row-polymorphic
    handle work(Unit) with
        | Prices.lookup(item) -> resume(Ashes.Catalog.priceCents(item))
        | Clock.now(_)        -> resume(Ashes.Time.unixSeconds(Unit))
        | Log.log(msg) ->
            let _ = Ashes.IO.writeLine("[log] " + msg)
            in resume(Unit)
        | return(r) -> r

let receipt = runProduction(fun _ -> processOrder("widget"))
in Ashes.IO.print(receipt.item + " total=" + Ashes.Text.fromInt(receipt.total))
```

(`Ashes.Catalog.priceCents` and `Ashes.Time.unixSeconds` are illustrative real-world primitives;
everything else is current Ashes plus the effect surface.)

## Worked example — test (the injection)

The **same** `processOrder` runs unchanged; only the handler differs. No real IO, no mocking
library, no parameters threaded through the call tree. As with the production example, this is shown
as two complete programs — explicit and implicit — that must both compile and print `PASS`. The test
handler's arms are pure (no real IO), so its inferred type is exact and can be written out in full in
the explicit version.

### Explicit — every annotation and `perform` written out

```
// expect: PASS

effect Prices =
    | lookup : Str -> Int
effect Clock =
    | now : Unit -> Int
effect Log =
    | log : Str -> Unit

type Receipt =
    | item: Str
    | base: Int
    | tax: Int
    | total: Int
    | stamp: Int

let taxFor : Int -> Int =
    fun cents -> cents / 10

let priceOf : Str -> Int uses {Prices} =
    fun item -> perform Prices.lookup(item)

let processOrder : Str -> Receipt uses {Prices, Clock, Log} =
    fun item ->
        let _ = perform Log.log("processing " + item)
        in
            let base = perform Prices.lookup(item)
            in
                let tax = taxFor(base)
                in
                    let total = base + tax
                    in
                        let _ = perform Log.log("total " + Ashes.Text.fromInt(total))
                        in
                            let t = perform Clock.now(Unit)
                            in Receipt(item = item, base = base, tax = tax,
                                       total = total, stamp = t)

// Pure handler -> the precise, open (row-polymorphic) type written out in full.
let runTest : (Unit -> Receipt uses {Prices, Clock, Log | e})
              -> (Receipt, List(Str)) uses e =
    fun work ->
        handle work(Unit) with
            | Prices.lookup(item) ->
                match item with
                    | "widget" -> resume(200)        // injected catalog
                    | _        -> resume(0)
            | Clock.now(_) -> resume(1000)           // frozen time (tail-resumptive)
            | Log.log(msg) ->
                match resume(Unit) with              // one-shot resumptive: collect logs on return
                    | (r, rest) -> (r, msg :: rest)
            | return(r) -> (r, [])

let result =
    match runTest(fun _ -> processOrder("widget")) with
        | (r, logs) ->
            match (r.base == 200, r.tax == 20, r.total == 220, r.stamp == 1000, logs) with
                | (True, True, True, True,
                   "processing widget" :: "total 220" :: []) -> "PASS"
                | _ -> "FAIL"
in Ashes.IO.print(result)
```

### Implicit — no `perform`, no operation/function/handler signatures

```
// expect: PASS

effect Prices = | lookup
effect Clock  = | now
effect Log    = | log

type Receipt =
    | item: Str
    | base: Int
    | tax: Int
    | total: Int
    | stamp: Int

let taxFor  = fun cents -> cents / 10         // : Int -> Int                        inferred
let priceOf = fun item -> Prices.lookup(item) // : Str -> Int uses {Prices}          inferred

let processOrder = fun item ->                // : Str -> Receipt uses {Prices, Clock, Log} inferred
    let _ = Log.log("processing " + item)
    in
        let base = priceOf(item)
        in
            let tax = taxFor(base)
            in
                let total = base + tax
                in
                    let _ = Log.log("total " + Ashes.Text.fromInt(total))
                    in
                        let t = Clock.now(Unit)
                        in Receipt(item = item, base = base, tax = tax, total = total, stamp = t)

// inferred: (Unit -> Receipt uses {Prices, Clock, Log | e}) -> (Receipt, List(Str)) uses e
let runTest = fun work ->
    handle work(Unit) with
        | Prices.lookup(item) ->
            match item with
                | "widget" -> resume(200)
                | _        -> resume(0)
        | Clock.now(_) -> resume(1000)
        | Log.log(msg) ->
            match resume(Unit) with
                | (r, rest) -> (r, msg :: rest)
        | return(r) -> (r, [])

let result =
    match runTest(fun _ -> processOrder("widget")) with
        | (r, logs) ->
            match (r.base == 200, r.tax == 20, r.total == 220, r.stamp == 1000, logs) with
                | (True, True, True, True,
                   "processing widget" :: "total 220" :: []) -> "PASS"
                | _ -> "FAIL"
in Ashes.IO.print(result)
```

`Clock.now(_) -> resume(1000)` is **tail-resumptive** — `resume` is the last thing the arm does, so
it compiles to a direct call through the handler evidence with no continuation capture. The `Log.log`
arm does work *after* `resume` returns, so it is **one-shot resumptive** and needs the state-machine
transform; because `resume` runs exactly once, it stays compatible with affine ownership.

## Type system

Effect rows are added to the Hindley-Milner system as a second kind of row alongside the existing
record rows, with row-polymorphic unification:

- **Operations** are typed like functions; their type is inferred by unifying all perform-sites and
  handler arms, then generalized with let-polymorphism.
- **A function's row** is the union of the rows of the operations it performs and the (open) rows of
  the functions it calls, minus any effects it handles internally.
- **A handler** removes the effects it has arms for from the row of its body and unifies all arm
  result types (including `return` and each `resume` continuation result) into the handle
  expression's type.
- **Unhandled effects:** if the top-level program's residual row is non-empty after the default
  built-in handlers are applied, that is a compile-time error (not a runtime `Unhandled`).
- **Annotation boundaries** mirror the rest of Ashes: infer locally, annotate at module exports and
  for intentionally-polymorphic operations.

## Continuations and the memory model

Continuation power is the single constraint that decides whether effects fit Ashes:

- **Tail-resumptive** (`resume` in tail position) — no continuation is captured at all; compiles to
  a dynamically-scoped call through the handler evidence. Covers the entire capabilities / DI story.
- **One-shot resumptive** (`resume` called exactly once, work after it) — the continuation is
  captured as a resumable state machine, reusing the async/await lowering. Consumed exactly once, so
  it satisfies affine ownership and deterministic destruction.
- **Multi-shot** (`resume` invoked more than once) — would require copying a captured slice of
  stack and heap, i.e. GC-style reachability. This violates Ground Rule 6 and breaks affine
  ownership (double-resume = double-use/double-drop of owned values; capture-without-resume has no
  deterministic drop point). **Out of scope.** Consequently effect-based generators, backtracking,
  and nondeterminism are not supported; the target programs (compiler, CLI, networking) effectively
  never need them.

## Injection model and prior art

Ashes uses **lexical handler injection** (the OCaml 5 / Koka / Eff / Frank / Unison family): the
nearest enclosing handler interprets an operation. Two other ML/FP injection routes are
deliberately **not** used because the language lacks their prerequisites:

- **Typeclass / monad-transformer injection** (Haskell `mtl`, tagless-final, `effectful`, `cleff`;
  Scala ZIO environments) — needs typeclasses/implicits, which Ashes does not have.
- **Functor injection** (Standard ML / OCaml functors — parameterize a module over a
  dependency-providing structure) — needs module functors, which Ashes does not have (inline modules
  are themselves only planned).

Relative to OCaml 5 specifically, Ashes adds what OCaml deliberately omitted: effects are tracked in
the type system, so an unhandled effect is a static error. Relative to Koka, Ashes restricts
continuations to one-shot for the no-GC reason above.

## Implementation plan (staged, mapped to the pipeline)

Per Ground Rule 1, `LANGUAGE_SPEC.md` is updated before any phase. Each stage is independently
useful and does not strand the next.

1. **Effect declarations + effect typing.** — DONE
   - Frontend: lex/parse `effect` declarations, effect type parameters, and `uses { ... }` rows in
     type annotations.
   - Semantics: introduce operation symbols (qualified `Effect.op`); extend HM inference with effect
     rows and row-polymorphic unification; compute and check function rows; emit the unhandled-effect
     diagnostic. No codegen yet — purely a typing layer (this also subsumes the old "IO marker"
     idea).

2. **Tail-resumptive handlers.** — DONE
   - Frontend: parse `handle ... with`, operation/`return` arms, optional `perform`, and `resume`.
   - Semantics: lower handlers to **evidence passing** — an implicit handler vector threaded through
     effectful calls; a tail-resumptive operation becomes a direct call through the evidence. No
     continuation capture. Delivers the full capabilities / DI / testing surface.
   - Backend: pass the evidence vector on the stack (no heap, no GC).

3. **One-shot resumptive handlers.**
   - Semantics: when an arm uses `resume` in non-tail position, capture the continuation by reusing
     and generalizing `StateMachineTransform.cs` (the async/await lowering). Integrate with
     `Lowering.Ownership.cs` so the continuation is affine (consumed once).
   - Diagnostics: a static or runtime guard that rejects a second `resume` of the same continuation.

4. **Multi-shot — not implemented.** Documented limitation with the memory-model rationale above.

Throughout: built-in effects (`IO`, etc.) ship with default top-level handlers installed by the CLI
entry so ordinary programs need no explicit `handle`; new `ASH####` diagnostics for unhandled
effects, double-resume, ambiguous/under-determined operations, and "polymorphic operation needs an
annotation"; and `.ash` tests + examples per Ground Rule 3, including the injection test above.

**Explicit/implicit conformance:** the optional-`perform` and optional-annotation decisions must be
backed by a paired test — the fully-explicit and fully-implicit production programs (both shown
above) must produce the **same inferred types, the same lowered IR, and the same program output**.
This is the executable proof that `perform` and the dropped signatures are no-op markers, and it
runs in both Stage 1 (types agree) and Stage 2 (lowering and output agree).
The paired tests are `tests/effect_conformance_explicit.ash` / `effect_conformance_implicit.ash`
(single-line output; the logging variant lives in `examples/effects_production.ash`); the
one-shot log-collecting test handler shown above becomes runnable with Stage 3.

## Implementation notes (Stage 2, as landed)

Handler evidence is **dynamically scoped**: the backend materializes one module global per
declared effect (`__ashes_effect_handler_<i>`, index = declaration order) holding a pointer to
the innermost installed handler frame for that effect, 0 when none. A `handle` expression
stack-allocates one frame per handled effect:

    [0 .. numEffects-1]            snapshot of every effect global, taken before any of this
                                   handle's frames install
    [numEffects + opDeclIndex]     one arm closure per operation (declaration order)

and installs it by writing the frame pointer into the effect's global; on body exit it restores
the global from the frame's own snapshot slot. A perform site loads the effect's global (O(1) —
no search), swaps **all** effect globals to the frame's snapshot, calls the arm closure with the
operation's arguments, and swaps back. The snapshot swap is what gives correct deep-handler
semantics: an arm runs under the evidence in scope at its handler's installation (with the
handler itself removed), so an arm performing its own effect reaches the next outer handler, and
handlers installed between the handler and the perform site are invisible to the arm. Typing
makes a missing handler unreachable; the emitted guard panics with a clear message rather than
dereferencing null if that invariant is ever broken.

A tail-resumptive arm compiles to an ordinary closure: every tail-position `resume(e)` is
rewritten to `e` ("resume with v" is exactly "return v to the perform site"), so there is no
continuation capture at all. `resume` outside tail position (one-shot, Stage 3) and tail paths
that do not resume (aborting arms, which need unwinding) are rejected with `ASH020`.

Typing (Stage 1) threads an **ambient effect row** through lowering: each lambda's arrow gets a
row variable that is the body's ambient row; operation calls insert their effect into it; an
applied arrow's open row unifies with the caller's ambient row while a written closed row only
subsumes into it (calling a `uses {Prices}` function from a `{Prices, Clock}` context is fine);
`handle` lowers its body under `{handled effects | t}` with `t` unified into the enclosing row.
Rows generalize with let-polymorphism; the ambient row's variables are treated as part of the
environment (the row analog of the value restriction). Unsigned operations infer monomorphically
within the compilation unit by unifying all perform-sites and handler arms.

Known limitations, to resolve in later stages:

- **Parallelism:** the evidence globals are per-process, not per-thread; installing or using
  handlers across `Ashes.Parallel.both` workers is unspecified (the TLS work under the
  parallelism milestone is the natural home for per-thread evidence).
- **Async:** a `handle` whose body suspends (`await`) is unspecified — handler frames are
  stack-allocated and do not survive coroutine suspension.
- **Modules:** effects are currently declared and used within one compilation unit (the project
  stitcher combines files into one unit, but exported-effect signatures across separate
  compilation are not yet enforced).
- **First-class operations:** a bare, uncalled operation reference (`let f = Clock.now`) is
  rejected ("must be called directly"), mirroring intrinsics.

## Open questions

- Exact `resume` surface for one-shot resumptive handlers (keyword vs. binding the continuation as a
  value), and whether a second resume is a compile-time or runtime error.
- Whether default built-in handlers are implicit at the CLI boundary only, or expressible in user
  code as well.
- Interaction with the existing `async`/`await` surface: re-express `await` as an effect handler, or
  keep it as sugar over Stage 3 for one release.

## Ground rules touched

- **Rule 1 (spec first):** the `effect`/`perform`/`handle`/`uses` surface and the row typing rules
  land in `LANGUAGE_SPEC.md` before implementation.
- **Rule 5 (purity preserved):** effects make impurity *visible and typed*; they do not introduce
  mutation. Handlers are pure functions.
- **Rule 6 (no GC):** the reason multi-shot continuations are out of scope; one-shot/tail-resumptive
  is exactly the subset expressible without a garbage collector.
