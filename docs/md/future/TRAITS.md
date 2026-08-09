# Traits: Principled Type-Directed Dispatch

## Status

Planned. This document records the agreed direction and the implementation tasks. It is not yet part
of the language specification; each shipped surface must move into
[`docs/md/reference/language.md`](../reference/language.md) before its implementation lands.

## Goal

Add a general-purpose static trait system with user-defined traits, coherent instances, inferred and
written constraints, generic conditional instances, supertraits, default methods, and dictionary
passing. All value operators whose meaning can be expressed as an ordinary strict function should
resolve through traits rather than compiler-specific overload rules.

Traits are broader than operator overloading. They must support ordinary type-directed APIs such as
`Show.show`, constrained recursive and higher-order functions, generic collection instances, and
cross-module constrained libraries.

## Agreed design

### Traits and capabilities are different concepts

Traits are static evidence selected by type. Capabilities are dynamically handleable effects selected
by scope. They remain separate in source and in the type system:

- trait requirements use `requires`;
- capability effects continue to use `needs`;
- trait instances cannot be installed with `handle`;
- instance selection never performs runtime lookup;
- trait and capability evidence may share compiler dictionary machinery where their requirements
  genuinely coincide;
- a trait method may declare a capability row, but an instance may not introduce effects absent from
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

instance Eq(Point) =
    | equal =
        given (left) ->
            given (right) ->
                // implementation

instance Eq(List(a)) requires {Eq(a)} =
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

- `trait`, `instance`, and `requires` are full keywords, not abbreviations;
- traits and instances are top-level declarations;
- trait methods are referenced qualified as `Trait.method` and may also back fixed operator syntax;
- method signatures are mandatory;
- default method implementations are allowed;
- `requires` on a trait declares supertraits;
- `requires` on an instance declares the evidence needed to construct that instance;
- `requires` on an annotated binding is part of its generalized type scheme, not a capability row;
- inferred non-recursive bindings may acquire and generalize constraints without a written annotation;
- exported, explicitly constrained recursive, and other module-boundary functions use written
  constraints so their evidence ABI is stable and visible.

Adding these keywords is a deliberate source-compatibility change. The language-reference task must
document it and update the former-keyword compatibility fixtures.

### Trait and instance model

- Traits may have one or more ordinary type parameters.
- Trait methods may mention any declared trait parameter.
- The initial implementation has no associated types, higher-kinded parameters, existential
  dictionaries, or local instances.
- An instance head may be concrete or generic, such as `Eq(List(a))`.
- Every constraint variable in an instance requirement must occur in the instance head.
- Instance requirements must be structurally smaller than the head so resolution terminates. Cyclic
  superclass graphs and cyclic or expanding instance resolution are compile-time errors.
- Default methods may call other methods of the same trait and inherited supertrait methods.
- An instance must supply every method that has no default, exactly once, and every implementation
  must match the declared method type and capability row.
- Trait dictionaries are ordinary immutable compiler-generated values. They are never compared by
  identity and never exposed as source values in the initial implementation.

### Coherence

Instance selection must have one answer for the whole resolved program:

- instances are visible program-wide across the complete dependency graph, regardless of imports;
- duplicate instance heads are rejected program-wide;
- overlapping heads are rejected by unification, including a generic/concrete pair such as
  `Eq(List(a))` and `Eq(List(Int))`;
- there is no priority, declaration-order, import-order, or "most specific" rule;
- an instance is legal only when its package defines the trait or defines at least one outer nominal
  type in the instance head;
- built-in types are owned by the core Ashes package for orphan-rule purposes;
- modules within one package may cooperate on instances, but package dependency order must not alter
  selection;
- separate packages cannot supply competing instances for a foreign trait and foreign type; a
  package that needs different behavior must introduce a nominal wrapper.

The project stitcher must retain instance provenance so diagnostics can name both conflicting package,
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

1. Unify the requested trait arguments with applicable instance heads.
2. Reject zero matches with a no-instance diagnostic.
3. Reject more than one match as an overlap/coherence failure; never choose by specificity.
4. Recursively resolve the selected instance requirements and supertraits.
5. Reject a repeated or non-decreasing resolution goal with a terminating diagnostic trace.
6. If the goal remains abstract inside a constrained function, thread its dictionary parameter.
7. If the goal is concrete, construct or directly specialize the selected instance.

A constraint is ambiguous when its type variables cannot be determined from the binding's ordinary
type, the expected type, or another resolved constraint. The initial implementation performs no
numeric defaulting to hide ambiguity. Numeric literals retain their current concrete types and suffix
rules.

### Evidence lowering

Abstract constrained functions receive hidden immutable dictionary parameters. Nested closures capture
those parameters through the ordinary closure ABI, and recursive or mutually recursive functions thread
them on every recursive edge. Concrete calls should specialize to direct method calls where the current
whole-program pipeline can prove the instance, but correctness must never depend on inlining.

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
ordering predicates from `compare`. The precise public `Ordering` ADT and comparison convention must be
specified in Task 1.

Before traits land, `!` is deliberately restricted to `Bool`. Trait migration moves it to `Not(a)`
using the same evidence rules as other strict unary operators; the bootstrap layer initially supplies
`Not(Bool)`. This does not introduce implicit truthiness: the operator consumes and returns the
instance type rather than coercing arbitrary values to `Bool`.

The following remain language operations rather than trait dispatch:

- `&&` and `||`, because they short-circuit evaluation;
- function application and the ordinary pipeline operator;
- Result pipelines, because they encode control flow and error propagation;
- list construction and pattern syntax;
- record access and functional record updates;
- assignment-like or mutation operators, which Ashes does not have.

`Str + Str` is an `Add(Str)` instance, not evidence that `Str` is numeric. A monolithic `Num` trait
must not force unsupported operations onto strings, unsigned values, or user types. A later convenience
`Num` supertrait may group compatible arithmetic traits without controlling operator resolution.

### Initial standard traits and instances

The initial standard trait layer includes:

- `Eq`, `Ord`, `Show`, `Hash`, `Default`, and `Not`;
- every arithmetic and bitwise operator trait in the table above;
- instances preserving all current behavior for `Int`, `Float`, `BigInt`, `u8`, `u16`, `u32`,
  `u64`, `Bool`, and `Str` where each operation is currently defined;
- conditional structural instances where meaningful for lists, tuples, `Maybe`, and `Result`;
- explicit user instances for nominal ADTs and records.

Float equality and ordering retain the current IEEE behavior. Integer division, remainder, overflow,
unsigned wrapping, shifts, string byte equality, and string concatenation must remain observably
identical during migration.

No default `Eq`, `Ord`, `Hash`, or `Show` instance is invented for functions, tasks, capabilities,
handlers, resources, or opaque external values. `Default` is supplied only where a canonical value is
part of the type's documented contract.

Automatic `deriving {Eq, Ord, Show, Hash}` for nominal ADTs and records is a follow-up layer built by
generating ordinary coherent instances. It does not use a second dispatch mechanism.

### Deferred extensions

These are intentionally outside the first trait implementation, but the AST and semantic model must
leave room for them:

- associated types;
- heterogeneous operator outputs such as `Matrix * Vector -> Vector`;
- higher-kinded trait parameters;
- local, scoped, overlapping, or incoherent instances;
- runtime trait-object or existential dispatch;
- user-defined operator tokens;
- overloaded numeric literals and numeric defaulting.

Heterogeneous operators should eventually use associated output types or an equally principled
functional-dependency design; they must not be approximated with ad-hoc overload searches.

## Implementation tasks

Tasks are ordered dependencies. Each task must keep the solution buildable and add focused tests; a task
must not silently land part of the next task's public behavior.

### Task 1: Write the normative trait specification

- [ ] Add the full grammar and semantics to `docs/md/reference/language.md` before parser changes.
- [ ] Specify `trait`, `instance`, `requires`, default method, and superclass syntax.
- [ ] Specify whether `requires` binds to a complete rank-1 type scheme and document its precedence
      relative to arrows and `needs` capability rows.
- [ ] Specify trait, method, type, capability, module, and value namespace interactions.
- [ ] Specify package-level orphan ownership, program-global visibility, overlap by unification,
      termination, and ambiguity rules.
- [ ] Specify primitive and structural instance behavior, including Float edge cases.
- [ ] Specify the operator mapping and the `Ordering` representation.
- [ ] Add accepted and rejected examples for inference, explicit constraints, generic instances,
      supertraits, defaults, effects in methods, overlap, orphans, ambiguity, and cycles.
- [ ] Update the keyword list and source-compatibility notes.

Acceptance: the reference answers every source-visible question without relying on this future document
or on implementation behavior.

### Task 2: Add frontend syntax and canonical formatting

Files primarily involved: `Ashes.Frontend/Tokens.cs`, `Lexer.cs`, `Parser.cs`, `Ast.cs`, `AstSpans.cs`,
and `Ashes.Formatter/Formatter.cs`.

- [ ] Add tokens and parser productions for trait declarations, instance declarations, method
      signatures/defaults, and constraint lists.
- [ ] Add immutable AST records for trait parameters, constraints, methods, instances, and source
      provenance.
- [ ] Attach written constraints to annotated binding/type syntax without encoding them as capabilities.
- [ ] Permit trait and instance declarations in the top-level program grammar and project modules.
- [ ] Define canonical indentation, line breaking, and ordering preservation in the formatter.
- [ ] Extend AST spans and parser recovery so malformed declarations produce bounded diagnostics.
- [ ] Add parser/formatter round-trip and formatter-idempotence tests for every new form.

Acceptance: syntax parses and formats canonically, but using it may still report a clear
"traits not enabled in semantics" diagnostic until the semantic tasks land.

### Task 3: Introduce trait symbols and constrained type schemes

Files primarily involved: `Lowering.TypeInference.cs`, `Lowering.Types.cs`, `Lowering.Symbols.cs`, and
the shared semantic type records.

- [ ] Add immutable trait, method, constraint, and instance-head semantic representations.
- [ ] Extend `TypeScheme` so quantified variables and canonical constraints are generalized,
      freshened, instantiated, substituted, and included in free-variable calculations together.
- [ ] Keep trait constraints separate from capability rows in `TypeRef.TFun`.
- [ ] Add deterministic pretty-printing for constrained types and combined `requires`/`needs` types.
- [ ] Record constrained hover types through the existing inference metadata path.
- [ ] Add unit tests for generalization, instantiation, substitution, occurs checks, and stable ordering.

Acceptance: constrained schemes preserve principal HM behavior and do not change unconstrained program
types or emitted code.

### Task 4: Register traits and instances across projects

Files primarily involved: `ProjectSupport.cs`, top-level registration, import/export metadata, and
module stitching.

- [ ] Register trait names, parameters, methods, defaults, and supertraits before value inference.
- [ ] Register instance heads with package/module/source provenance before resolving uses.
- [ ] Export traits as named module declarations while keeping instances program-global regardless of
      imports.
- [ ] Reject unknown traits, wrong trait arity, duplicate methods, missing required methods, duplicate
      implementations, and method signature mismatches.
- [ ] Enforce package-level orphan rules using manifest/package identity rather than filename layout.
- [ ] Detect duplicate and unifiable overlapping instance heads across all stitched modules.
- [ ] Detect superclass cycles before expression inference.
- [ ] Add deterministic multi-file and multi-package tests, including import-order independence.

Acceptance: the same resolved project always has the same legal instance registry regardless of source
or dependency traversal order.

### Task 5: Collect and infer constraints

- [ ] Make direct trait-method uses emit a constraint over the inferred receiver/type arguments.
- [ ] Make every mapped operator emit its corresponding trait constraint rather than selecting a
      primitive operation during inference.
- [ ] Generalize inferred constraints at non-recursive `let` boundaries.
- [ ] Check written `requires` clauses against inferred requirements at annotation boundaries.
- [ ] Require stable written constraints for exported constrained functions and dictionary-passed
      recursive groups where the normative spec requires them.
- [ ] Propagate constraints through calls, closures, partial application, higher-order arguments,
      matches, async bodies, and capability-performing functions.
- [ ] Diagnose constraints whose variables are ambiguous in the ordinary type.
- [ ] Preserve principal types for existing unconstrained programs.

Acceptance: examples such as a generic `contains` infer `Eq(a)` without any operator-specific inlining,
and missing/extra written constraints receive focused errors.

### Task 6: Implement coherent instance resolution

- [ ] Match constraints against instance heads using ordinary type unification without mutating the
      caller's inference state speculatively.
- [ ] Resolve conditional instance requirements and supertrait evidence recursively.
- [ ] Canonicalize equivalent constraints and remove supertraits implied by stronger constraints.
- [ ] Reject no-instance, multiple-match, cyclic, expanding, and depth-exhausted resolution with a
      complete requirement trace.
- [ ] Prove termination for generic instances using the normative structural-size rule.
- [ ] Cache resolved concrete goals deterministically without depending on process hash order.
- [ ] Add positive tests for nested structures and negative tests for every resolution failure.

Acceptance: `Eq(List(List(Point)))` resolves from the generic list instance and `Eq(Point)`, while an
overlapping or non-terminating instance set is rejected before lowering.

### Task 7: Extract shared static-evidence elaboration

Files primarily involved: `Lowering.CapabilityDictionaries.cs`, `Lowering.Capabilities.cs`, and a new
trait-focused lowering component.

- [ ] Separate reusable hidden-parameter/call-threading mechanics from capability-specific semantics.
- [ ] Elaborate every abstract constraint into a hidden immutable dictionary parameter with stable
      method order.
- [ ] Rewrite trait method calls to dictionary method values when evidence remains abstract.
- [ ] Build concrete instance dictionaries from implementations, defaults, requirements, and
      supertraits.
- [ ] Specialize concrete evidence to direct calls where safe, while retaining dictionary passing as
      the correctness path.
- [ ] Ensure default methods dispatch through the same selected dictionary and cannot accidentally
      resolve a different instance.
- [ ] Keep dynamic capability handler globals entirely out of trait lowering.

Acceptance: traits work when inlining and optimization are disabled, and enabling specialization does
not change observable behavior.

### Task 8: Complete recursive, higher-order, closure, and cross-module evidence passing

- [ ] Thread dictionaries through self recursion and every member of mutually recursive groups.
- [ ] Capture dictionaries in nested and escaping closures through the normal closure ABI.
- [ ] Thread constraints through partial application and constrained functions stored in aggregates.
- [ ] Remove the existing imported-qualified generic dictionary limitation from the capability path and
      prove the shared replacement for traits.
- [ ] Encode constrained exported-function ABI metadata so imported abstract calls receive evidence.
- [ ] Preserve evidence across async transformation when the declared method capability row permits it.
- [ ] Verify Perceus ownership, reuse, TCO, and closure-environment behavior for dictionaries containing
      closures or captured values.
- [ ] Add separate-module tests for concrete and still-abstract callers.

Acceptance: a constrained recursive collection function can be defined in one module, called abstractly
from a second, and instantiated in a third without inlining or wrapper workarounds.

### Task 9: Implement supertraits and default methods fully

- [ ] Type-check default method bodies under the trait parameters, self dictionary, declared
      supertraits, and declared capability rows.
- [ ] Materialize inherited evidence without duplicating or re-resolving instances.
- [ ] Permit an instance to override a default exactly once.
- [ ] Detect missing methods after defaults are applied.
- [ ] Detect default-method dependency cycles that would evaluate recursively without a base method.
- [ ] Add tests for inherited methods, default overrides, diamond-shaped supertrait requirements, and
      effect-row conformance.

Acceptance: `Ord(a)` supplies `Eq(a)` evidence and all default comparison operators without extra
instance declarations or runtime searches.

### Task 10: Add the standard trait layer and bootstrap instances

- [ ] Choose the shipped `Ashes.*` module location and export surface for standard traits.
- [ ] Add `Ordering`, `Eq`, `Ord`, `Show`, `Hash`, `Default`, and every mapped operator trait.
- [ ] Add primitive instances that exactly preserve current backend behavior.
- [ ] Add conditional structural instances for supported lists, tuples, `Maybe`, and `Result` shapes.
- [ ] Deliberately omit nonsensical instances for functions, tasks, effects, resources, and opaque
      externals.
- [ ] Ensure trait declarations and instances ship in every target's `lib/Ashes` and `dist` payload.
- [ ] Document every standard trait, method, law expectation, and supplied instance.

Acceptance: standard trait methods can be used directly before any source operator is migrated.

### Task 11: Migrate every suitable operator

Files primarily involved: `Lowering.Operators.cs`, overload inference, deferred comparisons,
`Lowering.TopLevel.cs`, and `BuiltinRegistry.cs`.

- [ ] Desugar each mapped operator to its standard trait method while preserving parsing precedence,
      associativity, source spans, and evaluation order.
- [ ] Cover equality, ordering, arithmetic, remainder, numeric negation, logical negation, bitwise
      operations including complement, and shifts.
- [ ] Keep short-circuit and control-flow operators on their existing dedicated paths.
- [ ] Preserve every current primitive edge case and diagnostic where the meaning remains applicable.
- [ ] Add user-defined nominal instances proving each operator family is genuinely extensible.
- [ ] Add differential IR/native tests comparing the old primitive behavior with trait-backed behavior
      during migration.
- [ ] Do not delete the old overload path until Task 14's compatibility gate passes.

Acceptance: no mapped operator requires overload-generic inlining for correctness, and a user type can
implement every suitable operator family through ordinary instances.

### Task 12: Migrate the standard library and generic APIs

- [ ] Rewrite `Ashes.Test.assertEqual` as an ordinary constrained function.
- [ ] Replace comparator-parameter APIs with constrained alternatives where doing so improves the
      public API; preserve explicit-comparator variants where custom orderings remain useful.
- [ ] Migrate collection equality, ordering, hashing, display, and defaults to standard traits.
- [ ] Ensure generic stdlib functions remain usable from imported modules at abstract types.
- [ ] Update examples, test directives, standard-library docs, and API signatures.
- [ ] Add compatibility tests for existing source that uses primitive operators and `assertEqual`.

Acceptance: no standard-library function depends on the overload-generic operator hack.

### Task 13: Add deriving as ordinary instance generation

- [ ] Specify `deriving {Eq, Ord, Show, Hash}` syntax for nominal ADTs and records in the language
      reference.
- [ ] Generate ordinary instance declarations with source provenance, not a special runtime path.
- [ ] Require the corresponding trait for every payload/field type.
- [ ] Define constructor and field ordering deterministically for `Ord`, `Show`, and `Hash`.
- [ ] Reject unsupported recursive or opaque fields with focused diagnostics.
- [ ] Format and expose derived constraints consistently in tooling.
- [ ] Add recursive ADT, parameterized ADT, record, cross-module, and negative tests.

Acceptance: manually written and derived instances obey identical coherence, resolution, lowering, and
ownership rules.

### Task 14: Remove overload hacks and close diagnostics/tooling

- [ ] Delete overload-generic registration, alias tracking, forced inlining, deferred operator patching,
      and any other path superseded by traits.
- [ ] Audit reuse-specialization and top-level inlining so removing the hack does not remove unrelated
      optimizations.
- [ ] Allocate and document trait diagnostics after checking the then-current lowest free `ASH0xx`
      codes.
- [ ] Include requirement traces and both source locations for coherence failures.
- [ ] Update LSP hover, completion, go-to-definition, references, semantic tokens, and formatting for
      traits, methods, instances, and constraints.
- [ ] Update `--emit-ir` and relevant `--explain` output to identify dictionary parameters and resolved
      instances without exposing unstable addresses.
- [ ] Update architecture documentation with the final evidence ABI and optimization path.

Acceptance: searching the compiler finds no correctness dependency on the old overload-generic
mechanism, and all public tooling understands the new declarations and types.

### Task 15: Add exhaustive validation and fuzz coverage

- [ ] Add focused unit suites for parsing, formatting, inference, scheme generalization, coherence,
      resolution, elaboration, ownership, operators, modules, diagnostics, and deriving.
- [ ] Add end-to-end `.ash` tests for every standard trait and operator family on every applicable
      primitive type.
- [ ] Add cross-module, cross-package, recursive, mutually recursive, higher-order, closure, async,
      capability, Perceus, reuse, and native-execution cases.
- [ ] Extend `Ashes.Fuzzing` with generated trait declarations, legal coherent instances, constrained
      functions, resolution traces, and trait/operator combination templates.
- [ ] Add invalid-source and invalid-semantic generation for malformed declarations, overlap, orphans,
      ambiguity, missing instances, and resolution cycles.
- [ ] Add differential oracles comparing specialized and unspecialized dictionary lowering.
- [ ] Run the full solution build, formatting gate, all C# test projects, all `.ash` tests, fuzz corpus,
      standard fuzz suite, `just ci-quick`, and `just ci`.

Acceptance: the implementation is deterministic, cross-module, optimization-independent, fuzzed in
combination with ownership/effects/async, and passes the complete repository gate.

## Definition of done

The trait feature is complete when:

- the normative language and architecture documentation describe the shipped behavior;
- user traits, defaults, supertraits, coherent concrete and generic instances, inferred/written
  constraints, and multi-parameter traits work across modules;
- constrained recursive, higher-order, closure-capturing, and effectful functions lower correctly;
- all suitable operators resolve through standard traits;
- primitive and structural behavior remains compatible;
- `assertEqual` and relevant collection APIs use ordinary constraints;
- derived instances use the same coherent mechanism as manual instances;
- the overload-generic inlining and deferred-operator correctness hacks are removed;
- diagnostics and LSP tooling expose useful trait information;
- formatter, semantic, IR, ownership, backend, native, and fuzz validation all pass;
- associated types, heterogeneous operator outputs, trait objects, and local/overlapping instances
  remain explicitly deferred rather than partially implemented.
