# Unified Capabilities: `capability` / `provide` / `needs` / `handle`

**Status:** Phases 1-3 shipped. The rename; static `provide` for concrete instances; generic resolution by monomorphization (non-recursive) and by dictionary passing (recursive/higher-order, on functions annotated `needs {Cap(a)}`); mixed dynamic+static `needs` rows. See LANGUAGE_SPEC.md section 20. Phase 4 (provider import/export across modules) remains.## Goal

Unify Ashes’ current algebraic effects model with a future type-directed constraint model under one language concept: **capabilities**.

The new terminology should be:

```text
capability = declares a contract of operations
provide    = supplies a static/type-directed implementation
needs      = declares that a function requires capabilities
handle     = supplies a scoped dynamic implementation
```

This replaces the current `effect` / `uses` language with more natural Ashes-style wording, while also opening the door for type-directed capabilities like ordering, hashing, JSON encoding, etc.

## Core Idea

Today effects are already close to capabilities:

```ash
effect Clock =
    | now : Unit -> Int

let stamp : Unit -> Int uses { Clock } =
    given (_) ->
        Clock.now(Unit)
```

This proposal changes that to:

```ash
capability Clock =
    | now : Unit -> Int

let stamp : Unit -> Int needs { Clock } =
    given (_) ->
        Clock.now(Unit)
```

Then type-directed contracts can use the same declaration form:

```ash
capability Ord(a) =
    | compare : a -> a -> Int

provide Ord(Str) =
    | compare = Ashes.String.compare
```

A generic function can require it:

```ash
let min : List(a) -> a needs { Ord(a) } =
    given (items) ->
        ...
```

## Desired Semantics

A capability requirement can be satisfied in exactly one of two ways:

```text
provide = static/type-directed satisfaction
handle  = dynamic/scoped satisfaction
```

If a required capability has no provider or handler, compilation fails.

If both a visible `provide` and an enclosing `handle` could satisfy the same required capability at the same call site, compilation should fail as ambiguous unless a deliberate override rule is later designed.

Do not silently prefer one over the other.

## Syntax

### Capability declaration

Replace:

```ash
effect Clock =
    | now : Unit -> Int
```

with:

```ash
capability Clock =
    | now : Unit -> Int
```

Parameterized capabilities are allowed:

```ash
capability Ord(a) =
    | compare : a -> a -> Int
```

This is the contract/interface: it says which operations must exist.

### Function requirements

Replace:

```ash
let f : A -> B uses { Clock } =
```

with:

```ash
let f : A -> B needs { Clock } =
```

Multiple requirements:

```ash
let timestampedMin : List(a) -> (Int, a) needs { Clock, Ord(a) } =
```

### Static provider

Add:

```ash
provide Ord(Str) =
    | compare = Ashes.String.compare

provide Ord(Int) =
    | compare = Ashes.Int.compare
```

This supplies static evidence for a concrete capability instance.

For now, providers should be top-level declarations only.

### Dynamic handler

Keep the existing `handle ... with` shape, but have it handle capabilities rather than effects:

```ash
handle stamp(Unit) with
    | Clock.now(_) ->
        resume(123)
```

`handle` remains the scoped dynamic implementation mechanism.

## Example

```ash
import Ashes.IO as io
import Ashes.List as list

capability Clock =
    | now : Unit -> Int

capability Ord(a) =
    | compare : a -> a -> Int

provide Ord(Str) =
    | compare = Ashes.String.compare

let min : List(a) -> a needs { Ord(a) } =
    given (items) ->
        match items with
            | [] ->
                io.panic("empty list")
            | x :: xs ->
                list.foldLeft(
                    given (best) ->
                    given (next) ->
                        if compare(next)(best) < 0
                        then next
                        else best
                )(x)(xs)

let timestampedMin : List(a) -> (Int, a) needs { Clock, Ord(a) } =
    given (items) ->
        let t = Clock.now(Unit)
        in
            let smallest = min(items)
            in
                (t, smallest)

handle timestampedMin(["Mattias", "Da", "Tobias"]) with
    | Clock.now(_) ->
        resume(123)
```

Expected value:

```text
(123, "Da")
```

## Migration From Existing Effects

Existing syntax:

```ash
effect Clock =
    | now : Unit -> Int

let f : Unit -> Int uses { Clock } =
    given (_) ->
        Clock.now(Unit)
```

New syntax:

```ash
capability Clock =
    | now : Unit -> Int

let f : Unit -> Int needs { Clock } =
    given (_) ->
        Clock.now(Unit)
```

The old words should either:

1. become parse errors with clear rename diagnostics, or
2. be temporarily accepted with warnings during migration.

Given Ashes’ current natural-keyword direction, prefer clear rename diagnostics.

Suggested diagnostics:

```text
`effect` has been renamed to `capability`.
`uses` has been renamed to `needs`.
```

## Compiler Model

Internally, this can initially reuse the existing effect infrastructure.

Current effect rows become capability rows.

Current effect declarations become capability declarations.

Current operation calls remain qualified operation calls:

```ash
Clock.now(Unit)
Ord.compare(x)(y)
```

The compiler must distinguish requirement satisfaction source:

```text
Capability requirement
    satisfied by handle  -> dynamic evidence path
    satisfied by provide -> static/type-directed evidence path
```

For the first implementation, it is acceptable to implement only the rename:

```text
effect -> capability
uses   -> needs
```

and leave `provide` / type-directed satisfaction as a follow-up phase.

However, the design should not bake in terminology that prevents `provide`.

## Type-Directed Capability Resolution

For `provide Ord(Str)`:

```ash
capability Ord(a) =
    | compare : a -> a -> Int

provide Ord(Str) =
    | compare = Ashes.String.compare
```

When compiling:

```ash
let sorted = list.sort(["b", "a"])
```

if `list.sort` has:

```ash
List(a) -> List(a) needs { Ord(a) }
```

then inference determines:

```text
a = Str
needs Ord(Str)
```

and resolves that requirement using:

```text
provide Ord(Str)
```

The generated code should be equivalent to passing or inlining `Ashes.String.compare`; there should be no runtime dictionary lookup unless the implementation chooses dictionary passing as an internal lowering strategy.

## Ambiguity Rule

If both exist:

```ash
provide Clock =
    | now = given (_) -> 123

handle stamp(Unit) with
    | Clock.now(_) -> resume(456)
```

and `stamp` needs `{ Clock }`, the compiler should reject the program as ambiguous.

No hidden precedence.

Suggested message:

```text
Capability 'Clock' is satisfied both by a provider and by a handler. Choose one.
```

## Open Questions

1. Should `provide` be allowed only for parameterized capabilities like `Ord(Str)`, or also for unparameterized capabilities like `Clock`?
2. Should a dynamic `handle` ever be allowed to override a static `provide` explicitly?
3. Should provided capabilities be imported/exported like top-level values?
4. Should providers live in the same module namespace as values, or in a separate provider/evidence namespace?
5. Should duplicate providers for the same concrete capability instance be a hard compile error? Recommended: yes.
6. Should `provide Ord(a)` generic providers be allowed eventually? Recommended: defer.

## Implementation Phases

### Phase 1 — Rename effects to capabilities — DONE

- Add `capability` keyword.
- Rename effect declarations to capability declarations.
- Rename `uses` rows to `needs` rows.
- Update parser, AST, formatter, diagnostics, language spec, architecture docs, and tests.
- Keep existing `handle` semantics unchanged.
- Existing effect behavior should remain semantically identical.

### Phase 2 — Add static `provide` — DONE

- Add top-level `provide Capability(args...) = ...` declarations.
- Validate that provided operation names match the capability declaration.
- Validate operation implementations match declared operation types.
- Detect duplicate providers for the same concrete capability instance.
- Add provider lookup during requirement resolution.

### Phase 3 — Type-directed capability propagation — DONE (concrete + monomorphized generics; recursive & higher-order via dictionary passing on annotated `needs {Cap(a)}` functions)

- Extend inference so `needs { Ord(a) }` propagates like current effect rows.
- Resolve concrete capability instances after type inference.
- Lower static capability calls to direct function calls / dictionaries / specialized code.
- Ensure no runtime handler evidence is emitted for statically provided capabilities.

### Phase 4 — Ambiguity and import/export rules

- Define and implement module visibility for providers.
- Decide whether providers must be imported explicitly.
- Reject ambiguous provider/handler satisfaction.
- Add diagnostics and tests.

## Tests to Add

### Rename tests

- `capability Clock = ...` parses.
- `needs { Clock }` typechecks.
- `handle ... with` still works.
- Old `effect` gives rename diagnostic.
- Old `uses` gives rename diagnostic.

### Dynamic capability tests

- A function needing `Clock` compiles only under a handler.
- Unhandled capability requirement gives compile error.
- Handler operation arity/type validation still works.
- One-shot and tail-resumptive handler behavior remains unchanged.

### Static provider tests

- `provide Ord(Str)` satisfies `needs { Ord(Str) }`.
- `provide Ord(Int)` satisfies `needs { Ord(Int) }`.
- Missing provider for `Ord(Foo)` fails.
- Duplicate `provide Ord(Str)` fails.
- Provider missing an operation fails.
- Provider operation with wrong type fails.

### Mixed tests

- Function with `needs { Clock, Ord(Str) }` compiles when `Clock` is handled and `Ord(Str)` is provided.
- Same function fails if `Clock` is neither handled nor provided.
- Same function fails if both provider and handler satisfy `Clock`.
- Type inference propagates `needs { Ord(a) }` through callers.

## Design Rationale

Ashes already has explicit effect requirements through `uses`. Type-directed abstractions such as ordering, hashing, formatting, and JSON encoding need a similar requirement mechanism.

Instead of adding a separate “trait/typeclass/constraint” feature with different terminology, this proposal treats both as capabilities:

```text
Clock    = capability satisfied dynamically by handle
Ord(Str) = capability satisfied statically by provide
```

This keeps the language small and natural:

```text
capability declares what can be done
needs declares what a function requires
provide supplies static evidence
handle supplies dynamic scoped evidence
```

It also avoids Haskell/Rust terminology such as `typeclass`, `instance`, `trait`, or `impl`, which would feel less consistent with Ashes’ natural-language keyword direction.
