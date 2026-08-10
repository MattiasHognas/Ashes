# Traits: Principled Type-Directed Dispatch

## Status

Tasks 1-15 implemented on `feature/traits`. A pre-PR verification pass (build, full test gates, a
three-way code audit against this checklist, and a `challenges/` regression run) found real gaps behind
several `[x]` marks: two confirmed miscompiles (one a crash) in evidence lowering, a severe memory
regression in a previously-fixed benchmark, and a set of smaller coherence/tooling/documentation defects.
Tasks 16-21 below capture that work; the branch is not ready for a PR until at least Task 16 and Task 17
are closed.

The normative source-visible design is now in
[`docs/md/reference/language.md`](../reference/language.md#21-traits-and-implementations). This document is
the ordered implementation checklist; if its prose differs from the language reference, the language
reference wins.

## Goal

Add a general-purpose static trait system with user-defined traits, coherent implementations, inferred and
written constraints, generic conditional implementations, supertraits, default methods, and dictionary
passing. All value operators whose meaning can be expressed as an ordinary strict function should
resolve through traits rather than compiler-specific overload rules.

Traits are broader than operator overloading. They must support ordinary type-directed APIs such as
`Show.show`, constrained recursive and higher-order functions, generic collection implementations, and
cross-module constrained libraries.

## Agreed design

### Traits and capabilities are different concepts

Traits are static evidence selected by type. Capabilities are dynamically handleable effects selected
by scope. They remain separate in source and in the type system:

- trait requirements use `requires`;
- capability effects continue to use `needs`;
- trait implementations cannot be installed with `handle`;
- implementation selection never performs runtime lookup;
- trait and capability evidence may share compiler dictionary machinery where their requirements
  genuinely coincide;
- a trait method may declare a capability row, but an implementation may not introduce effects absent from
  the declared method type.

### Proposed source surface

The exact grammar must be added to the language reference by Task 1. The intended shape is:

```ash
trait Eq(a) =
    | equal : a -> a -> Bool
    | notEqual : a -> a -> Bool =
        given (left) ->
            given (right) ->
                !Eq.equal(left)(right)

trait Ord(a) requires {Eq(a)} =
    | compare : a -> a -> Ordering

implement Eq(Point) =
    | equal =
        given (left) ->
            given (right) ->
                // implementation

implement Eq(List(a)) requires {Eq(a)} =
    | equal =
        given (left) ->
            given (right) ->
                // recursive structural implementation

let recursive contains : a -> List(a) -> Bool requires {Eq(a)} =
    given (needle) ->
        given (items) ->
            match items with
                | [] -> false
                | item :: rest ->
                    if item == needle then true else contains(needle)(rest)
```

Decisions represented by this surface:

- `trait`, `implement`, and `requires` are full keywords, not abbreviations;
- traits and implementations are top-level declarations;
- trait methods are referenced qualified as `Trait.method` and may also back fixed operator syntax;
- method signatures are mandatory;
- default method implementations are allowed;
- `requires` on a trait declares supertraits;
- `requires` on an implementation declares the evidence needed to construct that implementation;
- `requires` on an annotated binding is part of its generalized type scheme, not a capability row;
- inferred non-recursive bindings may acquire and generalize constraints without a written annotation;
- exported non-recursive bindings may infer constraints; canonical constraint ordering makes their
  generated evidence ABI and module metadata deterministic without requiring redundant annotations;
- constrained recursive groups use written constraints because their members must share a stable
  monomorphic inference boundary before their bodies are checked.

Adding these keywords is a deliberate source-compatibility change. The language-reference task must
document it and update the former-keyword compatibility fixtures.

### Trait and implementation model

- Traits may have one or more ordinary type parameters.
- Trait methods may mention any declared trait parameter.
- The initial implementation has no associated types, higher-kinded parameters, existential
  dictionaries, or local implementations.
- An implementation head may be concrete or generic, such as `Eq(List(a))`.
- Every constraint variable in an implementation requirement must occur in the implementation head.
- Implementation requirements must be structurally smaller than the head so resolution terminates. Cyclic
  superclass graphs and cyclic or expanding implementation resolution are compile-time errors.
- Default methods may call other methods of the same trait and inherited supertrait methods.
- An implementation must supply every method that has no default, exactly once, and every implementation
  must match the declared method type and capability row.
- Trait dictionaries are ordinary immutable compiler-generated values. They are never compared by
  identity and never exposed as source values in the initial implementation.

### Coherence

Implementation selection must have one answer for the whole resolved program:

- implementations are visible program-wide across the complete dependency graph, regardless of imports;
- duplicate implementation heads are rejected program-wide;
- overlapping heads are rejected by unification, including a generic/concrete pair such as
  `Eq(List(a))` and `Eq(List(Int))`;
- there is no priority, declaration-order, import-order, or "most specific" rule;
- an implementation is legal only when its package defines the trait or defines at least one outer nominal
  type in the implementation head;
- built-in types are owned by the core Ashes package for orphan-rule purposes;
- modules within one package may cooperate on implementations, but package dependency order must not alter
  selection;
- separate packages cannot supply competing implementations for a foreign trait and foreign type; a
  package that needs different behavior must introduce a nominal wrapper.

The project stitcher must retain implementation provenance so diagnostics can name both conflicting package,
module, and source locations.

### Constraint inference and resolution

The inferred type of an operator-using binding carries its constraint:

```ash
let equal = given (left) -> given (right) -> left == right
```

Conceptually infers:

```text
a -> a -> Bool requires {Eq(a)}
```

Constraints are collected while inferring expressions, simplified through supertraits, generalized at
ordinary non-recursive `let`, freshened with their quantified type variables, and instantiated at every
use. Constraint ordering in diagnostics, hover text, formatting, metadata, and dictionary layout is
canonical and deterministic.

Resolution rules:

1. Unify the requested trait arguments with applicable implementation heads.
2. Reject zero matches with a no-implementation diagnostic.
3. Reject more than one match as an overlap/coherence failure; never choose by specificity.
4. Recursively resolve the selected implementation requirements and supertraits.
5. Reject a repeated or non-decreasing resolution goal with a terminating diagnostic trace.
6. If the goal remains abstract inside a constrained function, thread its dictionary parameter.
7. If the goal is concrete, construct or directly specialize the selected implementation.

A constraint is ambiguous when its type variables cannot be determined from the binding's ordinary
type, the expected type, or another resolved constraint. The initial implementation performs no
numeric defaulting to hide ambiguity. Numeric literals retain their current concrete types and suffix
rules.

### Evidence lowering

Abstract constrained functions receive hidden immutable dictionary parameters. Nested closures capture
those parameters through the ordinary closure ABI, and recursive or mutually recursive functions thread
them on every recursive edge. Concrete calls should specialize to direct method calls where the current
whole-program pipeline can prove the implementation, but correctness must never depend on inlining.

The capability dictionary path in `Lowering.CapabilityDictionaries.cs` is reusable infrastructure, not
the trait semantic model. Before traits can ship, it must support constrained calls through imported
qualified functions at still-abstract types. The current capability limitation at that boundary is a
trait implementation blocker.

Evidence participates in ordinary ownership analysis. A dictionary containing closures or captured
values must receive correct Perceus duplication and dropping, survive tail calls and async lowering when
its method contract permits suspension, and never rely on process-global handler state.

### Operator mapping

All currently strict, value-producing overloadable operators move to focused traits. Their initial
method shapes preserve the current rule that both operands and the result have the same type unless the
operator currently returns `Bool`.

| Source operators | Standard trait | Required primitive operation |
|---|---|---|
| `==`, `!=` | `Eq(a)` | `equal : a -> a -> Bool` |
| `<`, `<=`, `>`, `>=` | `Ord(a)` | `compare : a -> a -> Ordering` |
| `+` | `Add(a)` | `add : a -> a -> a` |
| binary `-` | `Subtract(a)` | `subtract : a -> a -> a` |
| `*` | `Multiply(a)` | `multiply : a -> a -> a` |
| `/` | `Divide(a)` | `divide : a -> a -> a` |
| `%` | `Remainder(a)` | `remainder : a -> a -> a` |
| unary `-` | `Negate(a)` | `negate : a -> a` |
| `!` | `Not(a)` | `not : a -> a` |
| `&` | `BitAnd(a)` | `bitAnd : a -> a -> a` |
| `\|` | `BitOr(a)` | `bitOr : a -> a -> a` |
| `^` | `BitXor(a)` | `bitXor : a -> a -> a` |
| `<<` | `ShiftLeft(a)` | `shiftLeft : a -> a -> a` |
| `>>` | `ShiftRight(a)` | `shiftRight : a -> a -> a` |
| `~` | `BitwiseNot(a)` | `bitwiseNot : a -> a` |

`Ord(a)` has `Eq(a)` as a supertrait. Default methods implement `!=` from `equal` and derive the four
ordering predicates from `compare`. The normative specification uses
`Ordering = Less | Equal | Greater | Unordered`; the fourth case preserves IEEE NaN behavior.

Before traits land, `!` is deliberately restricted to `Bool`. Trait migration moves it to `Not(a)`
using the same evidence rules as other strict unary operators; the bootstrap layer initially supplies
`Not(Bool)`. This does not introduce implicit truthiness: the operator consumes and returns the
implement type rather than coercing arbitrary values to `Bool`.

The following remain language operations rather than trait dispatch:

- function application and the ordinary pipeline operator;
- Result pipelines, because they encode control flow and error propagation;
- list construction and pattern syntax;
- record access and functional record updates;
- assignment-like or mutation operators, which Ashes does not have.

`Str + Str` is an `Add(Str)` implementation, not evidence that `Str` is numeric. A monolithic `Num` trait
must not force unsupported operations onto strings, unsigned values, or user types. A later convenience
`Num` supertrait may group compatible arithmetic traits without controlling operator resolution.

### Initial standard traits and implementations

The initial standard trait layer includes:

- `Eq`, `Ord`, `Show`, `Hash`, `Default`, and `Not`;
- every arithmetic and bitwise operator trait in the table above;
- implementations preserving all current behavior for `Int`, `Float`, `BigInt`, `u8`, `u16`, `u32`,
  `u64`, `Bool`, and `Str` where each operation is currently defined;
- conditional structural implementations where meaningful for lists, tuples, `Maybe`, and `Result`;
- explicit user implementations for nominal ADTs and records.

Float equality and ordering retain the current IEEE behavior. Integer division, remainder, overflow,
unsigned wrapping, shifts, string byte equality, and string concatenation must remain observably
identical during migration.

No default `Eq`, `Ord`, `Hash`, or `Show` implementation is invented for functions, tasks, capabilities,
handlers, resources, or opaque external values. `Default` is supplied only where a canonical value is
part of the type's documented contract.

Automatic `deriving {Eq, Ord, Show, Hash}` for nominal ADTs and records is a follow-up layer built by
generating ordinary coherent implementations. It does not use a second dispatch mechanism.

### Deferred extensions

These are intentionally outside the first trait implementation, but the AST and semantic model must
leave room for them:

- associated types;
- heterogeneous operator outputs such as `Matrix * Vector -> Vector`;
- higher-kinded trait parameters;
- local, scoped, overlapping, or incoherent implementations;
- runtime trait-object or existential dispatch;
- user-defined operator tokens;
- overloaded numeric literals and numeric defaulting.

Heterogeneous operators should eventually use associated output types or an equally principled
functional-dependency design; they must not be approximated with ad-hoc overload searches.

## Implementation tasks

Tasks are ordered dependencies. Each task must keep the solution buildable and add focused tests; a task
must not silently land part of the next task's public behavior.

### Task 1: Write the normative trait specification

- [x] Add the full grammar and semantics to `docs/md/reference/language.md` before parser changes.
- [x] Specify `trait`, `implement`, `requires`, default method, and superclass syntax.
- [x] Specify whether `requires` binds to a complete rank-1 type scheme and document its precedence
      relative to arrows and `needs` capability rows.
- [x] Specify trait, method, type, capability, module, and value namespace interactions.
- [x] Specify package-level orphan ownership, program-global visibility, overlap by unification,
      termination, and ambiguity rules.
- [x] Specify primitive and structural implementation behavior, including Float edge cases.
- [x] Specify the operator mapping and the four-case `Ordering` representation required by IEEE NaN.
- [x] Add accepted and rejected examples for inference, explicit constraints, generic implementations,
      supertraits, defaults, effects in methods, overlap, orphans, ambiguity, and cycles.
- [x] Update the keyword list and source-compatibility notes.

Acceptance: the reference answers every source-visible question without relying on this future document
or on implementation behavior.

### Task 2: Add frontend syntax and canonical formatting

Files primarily involved: `Ashes.Frontend/Tokens.cs`, `Lexer.cs`, `Parser.cs`, `Ast.cs`, `AstSpans.cs`,
and `Ashes.Formatter/Formatter.cs`.

- [x] Add tokens and parser productions for trait declarations, implementation declarations, method
      signatures/defaults, and constraint lists.
- [x] Add immutable AST records for trait parameters, constraints, methods, implementations, and source
      provenance.
- [x] Attach written constraints to annotated binding/type syntax without encoding them as capabilities.
- [x] Permit trait and implementation declarations in the top-level program grammar and project modules.
- [x] Define canonical indentation, line breaking, and ordering preservation in the formatter.
- [x] Extend AST spans and parser recovery so malformed declarations produce bounded diagnostics.
- [x] Add parser/formatter round-trip and formatter-idempotence tests for every new form.

Acceptance: syntax parses and formats canonically, but using it may still report a clear
"traits not enabled in semantics" diagnostic until the semantic tasks land.

### Task 3: Introduce trait symbols and constrained type schemes

Files primarily involved: `Lowering.TypeInference.cs`, `Lowering.Types.cs`, `Lowering.Symbols.cs`, and
the shared semantic type records.

- [x] Add immutable trait, method, constraint, and implementation-head semantic representations.
- [x] Extend `TypeScheme` so quantified variables and canonical constraints are generalized,
      freshened, instantiated, substituted, and included in free-variable calculations together.
- [x] Keep trait constraints separate from capability rows in `TypeRef.TFun`.
- [x] Add deterministic pretty-printing for constrained types and combined `requires`/`needs` types.
- [x] Record constrained hover types through the existing inference metadata path.
- [x] Add unit tests for generalization, instantiation, substitution, occurs checks, and stable ordering.

Acceptance: constrained schemes preserve principal HM behavior and do not change unconstrained program
types or emitted code.

### Task 4: Register traits and implementations across projects

Files primarily involved: `ProjectSupport.cs`, top-level registration, import/export metadata, and
module stitching.

- [x] Register trait names, parameters, methods, defaults, and supertraits before value inference.
- [x] Register implementation heads with package/module/source provenance before resolving uses.
- [x] Export traits as named module declarations while keeping implementations program-global regardless of
      imports.
- [x] Reject unknown traits, wrong trait arity, duplicate methods, missing required methods, duplicate
      implementations, and method signature mismatches.
- [x] Enforce package-level orphan rules using manifest/package identity rather than filename layout.
- [x] Detect duplicate and unifiable overlapping implementation heads across all stitched modules.
- [x] Detect superclass cycles before expression inference.
- [x] Add deterministic multi-file and multi-package tests, including import-order independence.

Acceptance: the same resolved project always has the same legal implementation registry regardless of source
or dependency traversal order.

### Task 5: Collect and infer constraints

- [x] Make direct trait-method uses emit a constraint over the inferred receiver/type arguments.
- [x] Make every mapped operator emit its corresponding trait constraint rather than selecting a
      primitive operation during inference.
- [x] Generalize inferred constraints at non-recursive `let` boundaries.
- [x] Check written `requires` clauses against inferred requirements at annotation boundaries.
- [x] Infer canonical constraints for exported non-recursive functions and require stable written
      constraints only for dictionary-passed recursive groups.
- [x] Propagate constraints through calls, closures, partial application, higher-order arguments,
      matches, async bodies, and capability-performing functions.
- [x] Diagnose constraints whose variables are ambiguous in the ordinary type.
- [x] Preserve principal types for existing unconstrained programs.

Acceptance: examples such as a generic `contains` infer `Eq(a)` without any operator-specific inlining,
and missing/extra written constraints receive focused errors.

### Task 6: Implement coherent implementation resolution

- [x] Match constraints against implementation heads using ordinary type unification without mutating the
      caller's inference state speculatively.
- [x] Resolve conditional implementation requirements and supertrait evidence recursively.
- [x] Canonicalize equivalent constraints and remove supertraits implied by stronger constraints.
- [x] Reject no-implementation, multiple-match, cyclic, expanding, and depth-exhausted resolution with a
      complete requirement trace.
- [x] Prove termination for generic implementations using the normative structural-size rule.
- [x] Cache resolved concrete goals deterministically without depending on process hash order.
- [x] Add positive tests for nested structures and negative tests for every resolution failure.

Acceptance: `Eq(List(List(Point)))` resolves from the generic list implementation and `Eq(Point)`, while an
overlapping or non-terminating implementation set is rejected before lowering.

### Task 7: Extract shared static-evidence elaboration

Files primarily involved: `Lowering.CapabilityDictionaries.cs`, `Lowering.Capabilities.cs`, and a new
trait-focused lowering component.

- [x] Separate reusable hidden-parameter/call-threading mechanics from capability-specific semantics.
- [x] Elaborate every abstract constraint into a hidden immutable dictionary parameter with stable
      method order.
- [x] Rewrite trait method calls to dictionary method values when evidence remains abstract.
- [x] Build concrete implementation dictionaries from implementations, defaults, requirements, and
      supertraits.
- [x] Specialize concrete evidence to direct calls where safe, while retaining dictionary passing as
      the correctness path.
- [x] Ensure default methods dispatch through the same selected dictionary and cannot accidentally
      resolve a different implementation.
- [x] Keep dynamic capability handler globals entirely out of trait lowering.

Acceptance: traits work when inlining and optimization are disabled, and enabling specialization does
not change observable behavior.

### Task 8: Complete recursive, higher-order, closure, and cross-module evidence passing

- [x] Thread dictionaries through self recursion and every member of mutually recursive groups.
- [x] Capture dictionaries in nested and escaping closures through the normal closure ABI.
- [x] Thread constraints through partial application and constrained functions stored in aggregates.
- [x] Remove the existing imported-qualified generic dictionary limitation from the capability path and
      prove the shared replacement for traits.
- [x] Encode constrained exported-function ABI metadata so imported abstract calls receive evidence.
- [x] Preserve evidence across async transformation when the declared method capability row permits it.
- [x] Verify Perceus ownership, reuse, TCO, and closure-environment behavior for dictionaries containing
      closures or captured values.
- [x] Add separate-module tests for concrete and still-abstract callers.

Acceptance: a constrained recursive collection function can be defined in one module, called abstractly
from a second, and instantiated in a third without inlining or wrapper workarounds.

### Task 9: Implement supertraits and default methods fully

- [x] Type-check default method bodies under the trait parameters, self dictionary, declared
      supertraits, and declared capability rows.
- [x] Materialize inherited evidence without duplicating or re-resolving implementations.
- [x] Permit an implementation to override a default exactly once.
- [x] Detect missing methods after defaults are applied.
- [x] Detect default-method dependency cycles that would evaluate recursively without a base method.
- [x] Add tests for inherited methods, default overrides, diamond-shaped supertrait requirements, and
      effect-row conformance.

Acceptance: `Ord(a)` supplies `Eq(a)` evidence and all default comparison operators without extra
implement declarations or runtime searches.

### Task 10: Add the standard trait layer and bootstrap implementations

- [x] Choose the shipped `Ashes.*` module location and export surface for standard traits.
- [x] Add `Ordering`, `Eq`, `Ord`, `Show`, `Hash`, `Default`, and every mapped operator trait.
- [x] Add primitive implementations that exactly preserve current backend behavior.
- [x] Add conditional structural implementations for supported lists, tuples, `Maybe`, and `Result` shapes.
- [x] Deliberately omit nonsensical implementations for functions, tasks, effects, resources, and opaque
      externals.
- [x] Ensure trait declarations and implementations ship in every target's `lib/Ashes` and `dist` payload.
- [x] Document every standard trait, method, law expectation, and supplied implementation.

Acceptance: standard trait methods can be used directly before any source operator is migrated.

### Task 11: Migrate every suitable operator

Files primarily involved: `Lowering.Operators.cs`, overload inference, deferred comparisons,
`Lowering.TopLevel.cs`, and `BuiltinRegistry.cs`.

- [x] Desugar each mapped operator to its standard trait method while preserving parsing precedence,
      associativity, source spans, and evaluation order.
- [x] Cover equality, ordering, arithmetic, remainder, numeric negation, logical negation, bitwise
      operations including complement, and shifts.
- [x] Keep short-circuit and control-flow operators on their existing dedicated paths.
- [x] Preserve every current primitive edge case and diagnostic where the meaning remains applicable.
- [x] Add user-defined nominal implementations proving each operator family is genuinely extensible.
- [x] Add differential IR/native tests comparing the old primitive behavior with trait-backed behavior
      during migration.
- [x] Do not delete the old overload path until Task 14's compatibility gate passes.

Acceptance: no mapped operator requires overload-generic inlining for correctness, and a user type can
implement every suitable operator family through ordinary implementations.

### Task 12: Migrate the standard library and generic APIs

- [x] Rewrite `Ashes.Test.assertEqual` as an ordinary constrained function.
- [x] Replace comparator-parameter APIs with constrained alternatives where doing so improves the
      public API; preserve explicit-comparator variants where custom orderings remain useful.
- [x] Migrate collection equality, ordering, hashing, display, and defaults to standard traits.
- [x] Ensure generic stdlib functions remain usable from imported modules at abstract types.
- [x] Update examples, test directives, standard-library docs, and API signatures.
- [x] Add compatibility tests for existing source that uses primitive operators and `assertEqual`.

Acceptance: no standard-library function depends on the overload-generic operator hack.

### Task 13: Add deriving as ordinary implementation generation

- [x] Specify `deriving {Eq, Ord, Show, Hash}` syntax for nominal ADTs and records in the language
      reference.
- [x] Generate ordinary implementation declarations with source provenance, not a special runtime path.
- [x] Require the corresponding trait for every payload/field type.
- [x] Define constructor and field ordering deterministically for `Ord`, `Show`, and `Hash`.
- [x] Reject unsupported recursive or opaque fields with focused diagnostics.
- [x] Format and expose derived constraints consistently in tooling.
- [x] Add recursive ADT, parameterized ADT, record, cross-module, and negative tests.

Acceptance: manually written and derived implementations obey identical coherence, resolution, lowering, and
ownership rules.

### Task 14: Remove overload hacks and close diagnostics/tooling

- [x] Delete overload-generic registration, alias tracking, forced inlining, deferred operator patching,
      and any other path superseded by traits.
- [x] Audit reuse-specialization and top-level inlining so removing the hack does not remove unrelated
      optimizations.
- [x] Allocate and document trait diagnostics after checking the then-current lowest free `ASH0xx`
      codes.
- [x] Include requirement traces and both source locations for coherence failures.
- [x] Update LSP hover, completion, go-to-definition, references, semantic tokens, and formatting for
      traits, methods, implementations, and constraints.
- [x] Update the VS Code extension's TextMate grammar, language configuration, snippets, and syntax
      highlighting tests for `trait`, `implement`, `requires`, standard trait methods, and `deriving`.
- [x] Update `--emit-ir` and relevant `--explain` output to identify dictionary parameters and resolved
      implementations without exposing unstable addresses.
- [x] Update architecture documentation with the final evidence ABI and optimization path.

Acceptance: searching the compiler finds no correctness dependency on the old overload-generic
mechanism, and all public tooling understands the new declarations and types.

### Task 15: Add exhaustive validation and fuzz coverage

- [x] Add focused unit suites for parsing, formatting, inference, scheme generalization, coherence,
      resolution, elaboration, ownership, operators, modules, diagnostics, and deriving.
- [x] Add end-to-end `.ash` tests for every standard trait and operator family on every applicable
      primitive type.
- [x] Add cross-module, cross-package, recursive, mutually recursive, higher-order, closure, async,
      capability, Perceus, reuse, and native-execution cases.
- [x] Extend `Ashes.Fuzzing` with generated trait declarations, legal coherent implementations, constrained
      functions, resolution traces, and trait/operator combination templates.
- [x] Add invalid-source and invalid-semantic generation for malformed declarations, overlap, orphans,
      ambiguity, missing implementations, and resolution cycles.
- [x] Add differential oracles comparing specialized and unspecialized dictionary lowering.
- [x] Run the full solution build, formatting gate, all C# test projects, all `.ash` tests, fuzz corpus,
      standard fuzz suite, `just ci-quick`, and `just ci`.

Acceptance: the implementation is deterministic, cross-module, optimization-independent, fuzzed in
combination with ownership/effects/async, and passes the complete repository gate.

### Task 16: Fix silent-placeholder miscompiles in evidence lowering

Two independent, empirically confirmed bugs share one root cause: when trait evidence cannot be threaded
or resolved at a use site, `Lowering.Traits.cs` falls back to emitting `IrInst.LoadConstInt(placeholder, 0)`
and continuing as if nothing were wrong, instead of either constructing real evidence or reporting a
diagnostic. Both are reachable from ordinary, idiomatic source and were reproduced against the built
compiler, not just read from source.

- [ ] Fix `LowerBareTraitMethodReference` (`Lowering.Traits.cs:468-479`, reached via
      `LowerQualifiedVar` in `Lowering.ModuleResolution.cs:14-21`) so a trait method used as a first-class
      value (not immediately applied, not itself the direct right-hand side of a `requires`-annotated
      binding) either constructs a real dictionary-backed closure or is rejected with a focused
      diagnostic. Confirmed repro (segfaults, exit 139, on the built CLI):
      ```ash
      import Ashes.Trait
      import Ashes.Collection.List
      import Ashes.IO

      let results = List.map(Show.show)([1, 2, 3])
      match results with
          | first :: _ -> print(first)
          | [] -> print("empty")
      ```
- [ ] Fix the matching fallback in `Lowering.Traits.cs:635-642` and `:681-688` (the operator-desugaring
      path) so a constraint that remains a `TraitEvidencePlan.Parameter` with no enclosing
      `_activeTraitDictionaryParameters` entry raises a diagnostic (ambiguous constraint, `ASH010`, or
      no-implementation, `ASH036`, as appropriate) instead of emitting the same placeholder constant.
      Confirmed repro (compiles clean, prints the wrong answer, no diagnostic):
      ```
      ashes run --expr 'let f x = x in Ashes.IO.print(if f == f then "y" else "n")'
      ```
      prints `n` (should be a compile error: functions have no `Eq` implementation). Verify the
      previously-correct cases stay correct: concrete nominal/function-type constraints still resolve via
      `ASH036`, and legitimately-deferred higher-order constraints (a constraint resolvable once a caller
      supplies a concrete type, e.g. through a generic `apply` wrapper) still work.
- [ ] Fix the same-trait, multiple-type-variable dictionary collision: `BuildResolvedTraitDictionaryValues`
      (`Lowering.TraitEvidence.cs:1755-1767`), `TryMapTraitDictionaries` (`:1030-1047`),
      `CollectTraitMethodParameterNames` (`:947-961`), and `TryLowerActiveTraitMethod` (`:1137-1146`) all
      key dictionary lookup/threading by trait qualified name alone. A function such as
      `let f : a -> b -> Bool requires {Eq(a), Eq(b)}` therefore has its two `Eq` constraints alias onto
      one dictionary. Re-key by `(trait qualified name, canonical type-argument position)` so distinct
      constraints on the same trait never collide, or reject the shape at the `requires` boundary with a
      diagnostic if disambiguation is out of scope for this pass.
- [ ] Add regression tests for all three fixes: an unapplied trait method passed as a higher-order-function
      argument (the `List.map(Show.show)` shape above), an operator on a type with no possible
      implementation used both in an unannotated `let` and inside a constrained function body, and a
      function with two same-trait constraints over different type variables exercised at two different
      concrete types in the same program.

Acceptance: no source program causes a trait constraint to silently resolve to a placeholder constant;
every unresolvable-at-lowering-time constraint is either constructed correctly or reported as a diagnostic.

### Task 17: Restore constant-memory recognition through trait-dispatched operators

`challenges/fannkuch-redux` regressed from a documented flat ~8.2 MB peak RSS (any N) to memory scaling
with N! (N=8 -> 9.7 MB, N=9 -> 101 MB, N=10 -> 1.13 GB, N=11 -> 13.4 GB) after this branch's operator
migration. Output remains correct at every N tested — this is a memory-model regression, not a
correctness bug — but a >1000x RSS blowup at the documented workload is not shippable. This benchmark's
constant-memory behavior was previously fixed and is tracked as CO-29/CO-38 in
[the changelog](../internals/changelog.md); the working theory is that the accumulator-reset heuristics in
`Lowering.cs` (`IsStableAccumulatorExpr` / `GetTcoCopyOutKind`) recognized the loop's `==`/`<` comparisons
by their old direct-primitive IR shape, and no longer see through the indirect trait-method dispatch those
operators now desugar to, so the per-iteration arena reset silently stops qualifying.

- [ ] Confirm the root cause: instrument or trace `IsStableAccumulatorExpr`/`GetTcoCopyOutKind` against
      `challenges/fannkuch-redux/fannkuch-redux.ash` at a small N and verify whether reset-safety
      classification changes depending on `LoweringConfiguration.EnableTraitOperatorSpecialization`.
- [ ] Fix the classifier to see through primitive-specialized trait operator calls (the common case, where
      `ShouldUsePrimitiveOperatorSpecialization` already proved the operation reduces to a primitive), so
      reset-safety detection is unaffected by operator desugaring shape.
- [ ] Re-run `challenges/fannkuch-redux` at N=9, 10, 11 and confirm peak RSS returns to the flat baseline
      (~8.2 MB, independent of N).
- [ ] Investigate `challenges/k-nucleotide`, which is currently ~1.9x slower and ~1.7x higher peak RSS than
      its baseline while every other challenge on the same host ran faster than baseline — check whether
      trait-dispatched `compare` in `Ashes.Collection.Map`'s hot path shares this root cause. Fix together
      if so; file a separate follow-up if the cause is unrelated.
- [ ] Add a regression test (or a `challenges/` note plus a lightweight `.ash` test under `tests/`) that
      catches a future reset-safety classifier regression through trait dispatch, since `challenges/` is
      explicitly excluded from CI and would not otherwise catch this again.

Acceptance: `challenges/fannkuch-redux` is constant-memory again at every N, and the full
`challenges/README.md` baseline table is re-verified with no entry regressing time or peak RSS by more
than normal run-to-run noise.

### Task 18: Close trait fuzz coverage gaps in CI

Trait-aware fuzz generation (`TraitPreludeGenerator`, `TraitCombinationTemplates`,
`TraitInvalidCaseGenerator`, `DifferentialTraitEvidenceOracle`) is real and exercised by
`Ashes.Fuzzing.Tests`, but no profile that generates traits (`traits`, `traits-differential`,
`invalid-semantics`, or the `all` profile) is reachable from any automated gate: `ci/jobs.sh`'s `fuzz()`
runs `syntax, semantics, perceus, combinations, async, capabilities, resources, invalid-source, compile,
differential, corpus` and was not touched by this branch, and the pre-commit `smoke` profile has
`GenerateTraits: false` with `trait.*` combination templates explicitly filtered out.

- [ ] Add the `traits`, `traits-differential`, and `invalid-semantics` profiles to `ci/jobs.sh`'s `fuzz()`
      job (or an equivalent scheduled/manual gate if running them on every CI invocation is too slow;
      state the decision explicitly rather than leaving it implicit).
- [ ] If `smoke`'s exclusion of trait generation is intentional (fast pre-commit budget), document that
      reasoning next to the profile definition in `FuzzProfile.cs` so it doesn't read as an oversight.

Acceptance: at least one CI-reachable job exercises trait generation, coherent/incoherent implementation
combinations, and the specialized-vs-dictionary differential oracle; the exclusion of any trait profile
from a faster gate is a documented decision, not a gap.

### Task 19: Documentation and diagnostic-quality corrections

Smaller defects found by the audit that don't affect correctness but leave the spec, docs, or diagnostics
inconsistent with the shipped behavior.

- [ ] Add `deriving` to the reserved-keyword list in `docs/md/reference/language.md` (currently only
      `trait`, `implement`, `requires` are listed there, even though `deriving` is lexed as a keyword and
      is separately documented as reserved at `language.md:3333` and `:3427`).
- [ ] Add a worked accepted example for a default method and a rejected example for a default-method
      dependency cycle to the accepted/rejected examples list in `language.md` section 21.14; both rules
      currently exist only as prose.
- [ ] Correct the "tuples" wording in this document's Task 10 and in `docs/md/reference/standard-library.md`
      to state that structural tuple implementations cover 2-tuples only (`Trait.ash` has no 3+ arity
      implementations); either extend coverage or make the limitation explicit everywhere it's implied to
      be general.
- [ ] Document that `implement Ord(Str)` (enabling `<`/`<=`/`>`/`>=` on strings via byte comparison) is a
      new behavior, not preserved pre-existing behavior — call it out in the standard-library docs and this
      file's Task 10 rather than leaving it implied as a straight migration.
- [ ] Decide and act on the `Remainder(Float)` diagnostic regression: `5.5 % 2.0` now reports the generic
      `ASH036` no-implementation message instead of the previous primitive-specific `'%' requires
      Int%Int, unsigned%unsigned, or BigInt%BigInt` message. Either restore a tailored diagnostic for this
      case or accept `ASH036` and note the change in `docs/md/reference/diagnostics.md`.
- [ ] Fix the constrained-type pretty-printer (`Lowering.TypeInference.cs:433-447`) to print the actual
      `needs` keyword for capability rows instead of `uses`, which does not exist in the language. Correct
      the two tests that currently assert the wrong keyword as expected output
      (`TraitTypeSchemeTests.cs:103`, `TraitInferenceTests.cs:303`).
- [ ] Correct Task 12's claim that collection hashing was migrated to standard traits: `Collection.HashMap.ash:20`
      and `Collection.HashTrie.ash:8` still call `Ashes.Byte.hash` directly rather than taking
      `requires {Hash(K)}`, and `Hash` currently has no stdlib consumer at all. Either migrate these two
      modules or correct the checklist wording to describe what actually shipped.

Acceptance: the language reference, standard-library docs, and diagnostics reference accurately describe
the shipped behavior with no known contradictions; pretty-printed types never reference a nonexistent
keyword.

### Task 20: Test-coverage and tooling follow-ups

- [ ] Wire `vscode-extension/src/test/fixtures/traits.ash` into a real tokenization test (loading the
      grammar through `vscode-textmate` or the extension's existing token-inspection helper, not just
      regex-matching grammar patterns against sample strings as `syntaxHighlighting.test.ts` currently
      does), or delete the fixture if it's not going to be used.
- [ ] Review the unrelated change in `vscode-extension/src/extension.ts` that removes
      `context.subscriptions.push(client)` from the language-client registration — it rode along in this
      commit but has nothing to do with traits. Confirm it's intentional and safe (deactivate-time
      `client.stop()` still runs) or revert it as out-of-scope for this branch.
- [ ] Add a parser/formatter round-trip and idempotence test for a multi-parameter trait (e.g.
      `trait Convert(source, destination)`, a documented form in `language.md`) — currently untested.
- [ ] Add a formatter assertion (not just a parse-level test) for a recursive group's per-member `requires`
      clauses.
- [ ] Add at least one `tests/projects/` multi-file fixture exercising Task 8's full acceptance scenario
      (trait declared in one module, constrained function called abstractly from a second module,
      instantiated concretely in a third) with real computed-value assertions — the existing coverage for
      this scenario is in-process C# unit tests, several of which assert only that no diagnostics were
      raised rather than that the computed result is correct.
- [ ] Replace `TraitTypeSchemeTests.HasHoverMetadata` (`TraitTypeSchemeTests.cs:107-118`), which constructs
      a `HoverTypeInfo` by hand and asserts the field it just set, with a test that exercises the real
      `GetTypeAtPosition` hover path for a constrained binding, mirroring `TraitInferenceTests.cs:25-29`.
- [ ] Strengthen the Task 4 import-order-independence test
      (`TraitRegistrationTests.cs:184-195`) to assert order-independence of the actual implementation
      registry, overlap diagnostics, and resolution results — not just the set of registered trait
      qualified names.

Acceptance: the gaps above no longer read as untested claims; each has either real coverage or an explicit,
documented reason it's out of scope.

### Task 21: Coherence and resolution hardening

- [ ] Fix `_typeProvenanceByName` (`Lowering.cs:538`), which keys outer-type provenance for the orphan rule
      by unqualified type name. Two packages declaring a type with the same simple name can collide and
      silently grant or deny orphan rights to the wrong package. Key by package-qualified identity instead.
- [ ] Decide whether `ValidateInstanceRequirementTermination` should apply the strict structural-decrease
      rule to implementations with a fully concrete head, not only generic ones — it currently rejects a
      shape like `implement Show(Box) requires {Show(Int)}` (head size 2, requirement size 2, both
      concrete, so termination is not actually at risk). Either relax the rule for concrete heads and add a
      positive test, or keep it and document the stricter-than-normative-text behavior explicitly in
      `language.md` section 21.8.
- [ ] Resolve the dead-data gap in `TraitEvidencePlan.Instance.Requirements`: conditional implementation
      requirements are computed during resolution but never consumed during dictionary construction
      (requirements are re-resolved at the concrete goal inside the method body instead of being
      materialized as dictionary fields). Either wire the computed requirements into
      `BuildTraitDictionary`, or remove the dead computation and correct Task 7's wording so it describes
      the re-resolution design that actually shipped.

Acceptance: orphan-rule provenance cannot be confused across packages with colliding simple names;
the termination rule's actual scope matches its documentation; no computed evidence-resolution data is
silently discarded without either being used or being an intentional, documented design choice.

## Definition of done

The trait feature is complete when:

- the normative language and architecture documentation describe the shipped behavior;
- user traits, defaults, supertraits, coherent concrete and generic implementations, inferred/written
  constraints, and multi-parameter traits work across modules;
- constrained recursive, higher-order, closure-capturing, and effectful functions lower correctly;
- all suitable operators resolve through standard traits;
- primitive and structural behavior remains compatible;
- `assertEqual` and relevant collection APIs use ordinary constraints;
- derived implementations use the same coherent mechanism as manual implementations;
- the overload-generic inlining and deferred-operator correctness hacks are removed;
- diagnostics and LSP tooling expose useful trait information;
- formatter, semantic, IR, ownership, backend, native, and fuzz validation all pass;
- associated types, heterogeneous operator outputs, trait objects, and local/overlapping implementations
  remain explicitly deferred rather than partially implemented.
