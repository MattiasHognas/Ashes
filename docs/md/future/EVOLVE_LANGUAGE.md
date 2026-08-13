# Evolving the Ashes Language

Status: exploratory. This document identifies additions that are not part of the currently shipped
language and turns them into independently deliverable work. It is a roadmap, not a normative
specification: each milestone must update the relevant reference documentation before implementation.

The aim is not to broaden Ashes indiscriminately. These additions strengthen the language's existing
identity: pure immutable programs, inferred ownership, deterministic resource cleanup, explicit
capabilities, zero-cost abstraction, native executables, and no tracing garbage collector.

## Principles

- Preserve source-level purity and immutability. Representation reuse remains compiler-internal.
- Keep ordinary ownership implicit. Do not add general-purpose move, borrow, retain, drop, or lifetime
  syntax.
- Keep ordinary value destruction unobservable. Observable cleanup belongs only to affine external
  resources.
- Prefer compile-time abstraction and safety over runtime indirection.
- Preserve the strict project dependency graph: Frontend parses, Semantics defines meaning, Backend
  implements native representation, and CLI/LSP/Formatter consume those definitions.
- Deliver each milestone separately with its own specification, diagnostics, formatter/LSP support,
  and regression tests. Do not implement the whole roadmap as one feature branch.

## Recommended order

1. Explicit exports and opaque constructors.
2. Transparent type aliases and zero-cost nominal types.
3. User-defined affine external resources.
4. Ambient-authority capabilities for effectful builtins and FFI.
5. Structured asynchronous concurrency.
6. Unicode scalar values (`Rune`).
7. Pattern-language completion.

The order is intentional. Module abstraction is needed to hide zero-cost type and resource
representations; zero-cost types give later APIs precise domain types; user-defined resources
establish the ownership contract needed by scoped concurrency; and capability coverage makes the
authority of those APIs visible.

---

## Milestone 1: Explicit exports and opaque constructors

Status: completed.

### Current limitation

An imported file exports every top-level `let`, every top-level `type`, and all of that type's
constructors. Libraries cannot separate their public API from implementation helpers or expose a type
without exposing its representation. For example, persistent collections expose their internal tree
constructors and `Regex` exposes its raw handle wrapper.

### Intended contract

Add an optional module export declaration capable of exporting:

- a value;
- a type abstractly, without its constructors;
- a type together with selected or all constructors; and
- a nested module.

A file without an explicit export declaration retains today's export-all behavior. This makes the
feature source-compatible and allows libraries to migrate independently. Import selectors and
qualified access must consult the explicit interface, and hidden names must be indistinguishable from
other non-exported members outside the declaring module. Code inside the module retains full access.

An abstract type remains nominally identifiable to importers and may appear in public signatures,
trait constraints, and implementations. Importers cannot construct or pattern-match its hidden
constructors. Deriving and coherence continue to operate in the declaring package; an interface must
not create a second type identity.

The exact source spelling must be decided in the language-spec change. Prefer one declarative export
list over per-declaration visibility modifiers so the complete public surface is reviewable in one
place.

### Work items

1. Specify export-list grammar, duplicate/unknown export diagnostics, constructor-selection rules,
   nested-module behavior, and compatibility for files without an interface.
2. Extend the frontend AST and formatter with the chosen declaration.
3. Carry explicit export metadata through project compilation and replace export-all construction
   when metadata is present.
4. Make whole-module imports, aliases, selectors, qualified constructor expressions, constructor
   patterns, traits, and LSP symbol lookup honor the same filtered surface.
5. Migrate at least `Ashes.Collection.Map` and `Ashes.Text.Regex` so their representations and helper
   bindings are hidden while their documented APIs remain available.

### Acceptance gates

- Existing projects without explicit exports compile unchanged.
- Hidden values, constructors, and modules fail through qualified, unqualified, aliased, and selector
  access with one consistent diagnostic.
- Abstract types flow through public functions and trait constraints but cannot be constructed or
  destructured by importers.
- Formatter round trips, hover/completion omit hidden names, and project-mode tests cover path
  dependencies and nested inline modules.

---

## Milestone 2: Transparent aliases and zero-cost nominal types

Status: completed.

### Current limitation

Complex function signatures must be repeated structurally, while domain distinctions such as
`UserId` versus `OrderId` require allocating a regular one-constructor ADT. Ashes promises zero-cost
abstraction but has no source-level nominal wrapper guaranteed to erase to its payload representation.

### Intended contract

Provide two deliberately distinct declarations:

- A **transparent type alias** names a type expression and expands during type checking. It introduces
  no new type identity, constructor, value, or runtime representation.
- A **zero-cost nominal type** introduces exactly one field and one constructor. It is distinct
  during type checking but uses the payload's runtime representation with no tag or wrapper allocation.

Aliases may be parameterized, may appear wherever the expanded type may appear, and must reject direct
or indirect recursive alias cycles. Diagnostics and hovers should preserve a useful alias name while
also showing its expansion when needed.

Zero-cost types support ordinary construction and single-constructor pattern matching, explicit
deriving, module abstraction, and coherent trait implementations. They do not implicitly coerce to
or from the payload type. A zero-cost type containing a resource has the same affine classification
as that resource; otherwise its ownership/layout capability is exactly the payload capability.

The selected syntax is `type alias Name(a) = ...` for transparent aliases and
`type Name(a) = Constructor(payload)` for zero-cost nominal types. The absence of `|` distinguishes
the zero-cost form from an ordinary one-constructor ADT
(`type Name(a) = | Constructor(payload)`). No additional keyword is reserved.

### Work items

1. Specify alias expansion, parameter scope, cycle detection, error rendering, zero-cost type
   nominality, construction, matching, deriving, and representation erasure.
2. Add distinct frontend nodes rather than encoding either feature as a special ordinary `TypeDecl`.
3. Resolve aliases before ordinary unification while retaining display metadata; add cycle-safe
   expansion shared by annotations, externals, constraints, and imported signatures.
4. Teach heap-layout, ownership, resource containment, trait deriving, and backend ABI lowering to
   treat a zero-cost type as its payload after semantic type checking.
5. Add formatter, hover, completion, go-to-definition, and semantic-token support.

### Acceptance gates

- Aliases are type-identical to their expansions and produce no IR or symbols of their own.
- Zero-cost types reject accidental interchange with their payload or another same-payload zero-cost
  type.
- Emitted layout and calling convention are byte-for-byte identical to the payload representation on
  every target, including resource payloads and FFI-safe primitive payloads.
- Recursive alias cycles, invalid zero-cost type shapes, hidden constructors, and deriving failures
  have stable diagnostics.

---

## Milestone 3: User-defined affine external resources

Status: completed.

### Current limitation

Deterministic cleanup applies to compiler-known handles such as `FileHandle`, `Socket`, `TlsSocket`,
and `Process`. A package binding a native database, parser, archive, image, or window handle cannot
declare that the handle must be released exactly once or connect an external destructor to automatic
scope cleanup.

### Intended contract

Extend opaque external types with an opt-in **resource** classification and one destructor external
symbol. Values of such a type participate in the existing affine resource rules:

- ownership moves when stored, returned, captured, spawned, or passed to a consuming parameter;
- read-only calls may borrow when their external signatures explicitly permit it;
- explicit destruction marks the value closed;
- every remaining live ownership path invokes the destructor exactly once at scope exit; and
- resource-bearing aggregates inherit the same classification and recursive cleanup rules.

FFI ownership cannot be inferred by inspecting native code. External parameters that receive a
resource must therefore declare whether the call borrows or consumes it. Keep this syntax confined to
external declarations; ordinary Ashes functions continue using inferred borrowing. Return values are
owned unless the declaration explicitly models a borrowed view in a later, separately specified
extension.

This milestone must not add finalizers to ordinary ADTs, records, strings, closures, or RC cells.
Ordinary destruction timing remains unobservable.

### Work items

1. Specify resource-type declarations, destructor signature constraints, explicit-close behavior,
   FFI borrow/consume markers, and invalid destructor diagnostics.
2. Register declared resources in the same semantic classification used by built-in resources rather
   than adding a parallel ownership system.
3. Generalize resource drop synthesis, aggregate traversal, move analysis, branch joins, task-frame
   cleanup, and explicit-close tracking to use declared resource metadata.
4. Lower destructor calls through the existing external-call/linking surface on all four targets.
5. Expose resource classification and parameter ownership in `--explain ownership` and LSP hover.
6. Add a hermetic test native library with a counted handle/destructor; do not depend on a machine-
   installed third-party library.

### Acceptance gates

- Straight-line, branch, match, recursive aggregate, closure capture, task frame, explicit close, and
  error-return paths each release exactly once.
- Use-after-close, double-close, and use-after-move diagnostics apply exactly as they do to built-in
  resources.
- Borrowing external calls preserve caller ownership; consuming calls reject later caller use.
- Destructor ABI and linking tests cover Linux x64/arm64 and Windows x64/arm64 structurally, with
  executable coverage wherever the target can run.
- Memory-growth and descriptor-count tests prove bounded cleanup under repeated construction.

---

## Milestone 4: Ambient-authority capabilities

Status: completed.

### Current limitation

`NetListen`, `NetConnect`, and `Stop` make some runtime authority visible in capability rows, but
console access, filesystem acquisition, process creation, clocks, entropy, and arbitrary external
calls can still appear in capability-free function types. Closed rows therefore cannot yet describe a
complete authority boundary, and registry capability metadata is incomplete.

### Intended contract

Introduce runtime-provided marker capabilities for acquiring ambient authority. Initial categories:

- `ConsoleIO` for terminal/stdin/stdout access;
- `FileRead` and `FileWrite` for opening, reading, mapping, creating, and replacing filesystem data;
- `ProcessSpawn` for spawning or controlling processes;
- `TimeRead` for wall or monotonic time acquisition;
- `Entropy` for nondeterministic seed acquisition; and
- `UnsafeFfi` for arbitrary user external calls unless a more specific declared capability is
  attached to that external declaration.

Possession-based operations remain capability-free. Once code owns a socket, file handle, process, or
other resource, using or closing that resource is authority conveyed by possession. Marker
capabilities govern acquiring ambient access, not manipulating already-owned values.

Like the existing network markers, these capabilities are supplied by the native runtime and excluded
from the top-level unsatisfied-capability error. They still propagate through inference and are checked
against written closed rows. User handlers must not intercept them unless a later proposal defines a
safe virtualized runtime interface.

This is source-breaking for closed capability rows that currently omit an effect they perform. Land it
only in a declared compatibility release, with precise migration diagnostics.

### Work items

1. Audit every builtin and FFI entry point, assigning ambient acquisition capabilities while leaving
   possession-based operations unmarked.
2. Specify the built-in names, inference behavior, closed-row migration, external-declaration
   capability syntax, and conflicts with user capability declarations.
3. Attach rows to builtin/external type schemes and ensure higher-order, recursive, trait-method,
   provider, async, and project-stitching paths propagate them unchanged.
4. Extend package publication metadata and compiler reports so the inferred authority surface is
   inspectable.
5. Add migration documentation with before/after examples for closed rows.

### Acceptance gates

- A closed pure row rejects hidden file, process, console, clock, entropy, network-acquisition, and
  unsafe-FFI effects, including effects reached through higher-order functions and trait methods.
- Existing possession-only helpers do not acquire spurious capabilities.
- Registry metadata and `--explain` output agree with inferred rows.
- Open-row programs retain inferred behavior; breaking changes are limited to code that asserted an
  incomplete closed row.

---

## Milestone 5: Structured asynchronous concurrency

Status: completed.

### Current limitation

`Ashes.Task.spawn` detaches a task and returns `Unit`. Callers cannot join it, observe its failure, or
bound its lifetime. Detached tasks still running when `Task.run` completes are abandoned. This is
useful for server internals but is a poor default for application concurrency and weakens Ashes'
otherwise deterministic lifetime story.

### Intended contract

Add a scoped task-group abstraction and an affine join handle. Conceptually the API provides:

```text
Task.scope : Task(E, A) -> Task(E, A)
Task.fork  : Task(E, A) -> Task(E, JoinHandle(E, A))
Task.join  : JoinHandle(E, A) -> Task(E, A)
```

The exact type/error spelling belongs in the spec, but these invariants are required:

- a child cannot outlive its scope;
- every `async` activation is an implicit scope, and `scope` optionally introduces a shorter nested
  boundary without exposing a scope token;
- a `JoinHandle` cannot escape the async or explicit scope that owns it, including through a returned
  aggregate or a detached-task capture;
- leaving the scope cancels and drains every unjoined child before returning;
- joining consumes the handle exactly once and yields the child's result;
- scope cancellation recursively cancels child scopes and releases task frames/resources;
- a parent failure cancels siblings before propagating; and
- cancellation is observed only at existing scheduler suspension points, preserving cooperative
  scheduling and no shared mutation.

`spawn` remains available as an explicitly detached primitive for infrastructure such as socket
servers. Documentation should steer ordinary user code toward scoped concurrency.

### Work items

1. Specify scope lifetime, child error propagation, join consumption, cancellation order, empty
   scopes, nested scopes, and interaction with `race`, `all`, and resources.
2. Add the built-in affine `JoinHandle(E, A)` type, resource diagnostics, implicit scope inference for
   `async`, and a semantic non-escape check (the ordinary HM type alone cannot express this lifetime).
3. Extend scheduler/task-frame state with parent-child linkage owned by the task region, not cyclic RC
   graphs.
4. Implement fork, join, scope-exit cancellation/drain, and nested cancellation on Linux and Windows.
5. Make task-frame ownership descriptors release captured ordinary values and resources on every
   completion/cancellation path.
6. Add structured-concurrency explain output and debugger/LSP type visibility.

### Acceptance gates

- Successful join, child failure, parent failure, early scope return, nested scope, parked socket,
  parked timer, and unjoined child paths all terminate and clean up deterministically.
- Double join and use-after-join are compile-time resource errors.
- Repeated cancellation has flat memory and file-descriptor/socket counts.
- Cross-target scheduler tests prove identical observable results; races may choose different winners
  only where `race` already permits that behavior.

---

## Milestone 6: Unicode scalar values (`Rune`)

Status: completed.

### Current limitation

Text APIs represent a single character as `Str`. A caller can pass `""` or `"several"` to character
predicates, and the type system cannot distinguish a Unicode scalar from arbitrary text. Ashes already
documents codepoint-indexed text operations, so this missing distinction leaks into otherwise precise
APIs.

### Intended contract

Add `Rune`, an inline copy type representing exactly one valid Unicode scalar value. Use the name
`Rune`, not `Char`: a scalar is not necessarily one user-perceived grapheme cluster.

Provide rune literals with escaped forms, and make invalid scalar literals compile-time errors
(surrogate code points and values above Unicode's maximum). Core conversions should include:

```text
Text.uncons   : Str -> Maybe((Rune, Str))
Rune.toText   : Rune -> Str
Rune.toInt    : Rune -> Int
Rune.fromInt  : Int -> Maybe(Rune)
```

Character classification should move to rune-typed functions. Preserve explicitly ASCII-scoped
operations where their current contract is ASCII; do not promise locale-sensitive casing or grapheme
segmentation in this milestone.

Changing `Text.uncons` is source-breaking. Either provide a compatibility helper under a distinct
name for one release or land the signature change in the same declared compatibility release as the
ambient-capability changes.

This milestone uses single-quoted literals (`'a'`, `'😀'`, `'\n'`, `'\u{1F600}'`) and provides
`Text.unconsText : Str -> Maybe((Str, Str))` as the one-release compatibility helper. Character
classification moves to the explicitly scoped `Rune.isAsciiLetter`, `Rune.isAsciiDigit`, and
`Rune.isAsciiWhiteSpace` APIs.

### Work items

1. Specify literals, escapes, scalar validity, conversions, comparison/hashing/showing, and the exact
   migration of existing `Str -> Bool` character predicates.
2. Add lexer/parser/formatter support and a semantic inline-copy type.
3. Implement UTF-8 decode/encode at `Text.uncons` and conversion boundaries without heap allocation
   for the `Rune` itself.
4. Add `Eq`, `Ord`, `Show`, `Hash`, and `Default` only where a canonical contract exists; do not invent
   a default rune merely to satisfy the trait.
5. Update text modules, examples, self-hosting code, hover/completion, and documentation.

### Acceptance gates

- ASCII, two-/three-/four-byte scalars, escapes, invalid UTF-8 boundaries, surrogates, and maximum
  scalar values are covered.
- `Rune` is a copy value and adds no allocation to text traversal.
- Round trips `Rune -> Str -> Rune` and valid `Int -> Rune -> Int` are exact.
- APIs cannot receive empty or multi-scalar strings where one scalar is required.

---

## Milestone 7: Pattern-language completion

### Current limitation

Records are constructed and updated by field name but destructured positionally. Patterns also cannot
retain the whole matched value while destructuring it, or combine alternatives that share one body.
This causes repetition and makes record matches depend unnecessarily on field declaration order.

### Intended contract

Add three orthogonal pattern forms:

- **Named record patterns**, matching fields by name and allowing omitted fields.
- **As-patterns**, binding the complete matched value in addition to nested bindings.
- **Or-patterns**, allowing alternatives only when every alternative binds the same names at the same
  inferred types.

Named record patterns are nominal: the record type/constructor must be known, field names must exist,
and a field may appear at most once. Omitted fields are ignored. Or-patterns must not change arm
evaluation order, guard semantics, or exhaustiveness. As-patterns bind one additional alias whose
ownership follows the existing implicit-sharing rules for ordinary values and affine rules for
resource-bearing values.

Do not add view/active patterns in this milestone. Arbitrary function execution during matching would
complicate purity, capability rows, exhaustiveness, and evaluation order.

### Work items

1. Specify precedence and grammar, binder-set equality for or-patterns, record-field rules,
   exhaustiveness/redundancy behavior, and resource-binding restrictions.
2. Add explicit AST cases, spans, formatter support, and parser recovery.
3. Extend pattern type checking and decision-tree/exhaustiveness analysis without desugaring away
   source identities needed by diagnostics.
4. Extend lowering ownership transfer/duplication for aliases and alternative binders.
5. Add completion, hover, rename, semantic tokens, and diagnostics for fields and binders.

### Acceptance gates

- Nested named-record, constructor, tuple, and list patterns compose.
- Or-pattern binder names/types must match exactly; guards run once after the selected alternative.
- Exhaustiveness and redundant-arm diagnostics remain correct with nested alternatives.
- As-patterns over shared ordinary values and resource-bearing aggregates neither leak nor double-drop.
- Formatter round trips every new pattern form canonically.

---

## Follow-on library work

These are useful after the foundational milestones but do not independently justify core syntax:

- A pure deterministic PRNG value, with nondeterministic seeding behind `Entropy`.
- Property-based testing built from pure generators, deterministic seeds, shrinking, and replay.
- Zero-cost nominal `Duration`, `Instant`, and `Path` domain types.
- A concurrent task-collection combinator implemented on structured task scopes, distinct from the
  current sequential `Task.all` contract.
- `NonEmptyList` and validation combinators as ordinary library ADTs.
- A safe `Ashes.Byte.getMaybe` alongside the existing panicking indexed operation.

## Explicit non-goals and separately tracked work

This roadmap does not propose:

- mutation, mutable references, null, exceptions, or a tracing garbage collector;
- general lazy evaluation;
- multi-shot continuations, backtracking handlers, or generator effects;
- observable finalizers for ordinary immutable values;
- an implicit prelude;
- unrestricted macros or arbitrary compile-time execution;
- runtime trait objects, overlapping/incoherent implementations, or other trait extensions already
  listed as deferred in the language reference;
- Set, generic hashing, string builders, streaming HTTP request bodies, FFI structs/callbacks/varargs,
  or other gaps already recorded in existing future/reference documents; or
- [self-hosting](SELF_HOSTING.md) and [WebAssembly](WASM_TARGET.md), which have their own roadmaps.

Any item promoted from this document must first be made normative in the language, standard-library,
CLI, diagnostics, and architecture references appropriate to its surface. Completed implementation
history belongs in the compiler changelog, not in this roadmap.
